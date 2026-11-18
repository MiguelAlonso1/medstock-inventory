using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MedStock.Web.Services.Assistant;

/// <summary>
/// Google Gemini API (free tier) via native generateContent function calling over HttpClient + System.Text.Json.
/// Key in header x-goog-api-key, streaming through streamGenerateContent?alt=sse. The model's raw parts are replayed
/// unchanged on the next turn (Gemini 3 requires thought signatures to be echoed back).
/// </summary>
public sealed class GeminiProvider(HttpClient http, IConfiguration cfg) : IAiProvider
{
    public const string DefaultModel = "gemini-3.5-flash-lite";

    public string Name => "Gemini";
    public string? ApiKey => cfg["Ai:Gemini:ApiKey"];
    public bool Enabled => !string.IsNullOrWhiteSpace(ApiKey);
    public string Model => cfg["Ai:Gemini:Model"] is { Length: > 0 } m ? m : DefaultModel;
    private string BaseUrl => cfg["Ai:Gemini:BaseUrl"] is { Length: > 0 } b ? b.TrimEnd('/') : "https://generativelanguage.googleapis.com";

    public async Task<AiResponse> CompleteAsync(string system, IReadOnlyList<AiMessage> messages, bool allowTools, Func<string, Task> onText, CancellationToken ct)
    {
        if (!Enabled) throw new InvalidOperationException("Gemini API key is not configured.");

        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system }) },
            ["contents"] = ToWire(messages),
            ["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = Declarations() }),
            ["toolConfig"] = new JsonObject { ["functionCallingConfig"] = new JsonObject { ["mode"] = allowTools ? "AUTO" : "NONE" } },
            ["generationConfig"] = new JsonObject { ["maxOutputTokens"] = 4096 }
        };

        var url = $"{BaseUrl}/v1beta/models/{Uri.EscapeDataString(Model)}:streamGenerateContent?alt=sse";
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("x-goog-api-key", ApiKey);

        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
            throw new AiProviderException((int)res.StatusCode, ExtractError(await res.Content.ReadAsStringAsync(ct)));

        var text = new StringBuilder();
        var rawParts = new JsonArray();
        var calls = new List<AiToolCall>();
        string? finish = null;
        var blocked = false;

        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data.Length == 0) continue;
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
                throw new AiProviderException(err.TryGetProperty("code", out var c) && c.TryGetInt32(out var code) ? code : 500, "stream error");
            if (root.TryGetProperty("promptFeedback", out var pf) && pf.TryGetProperty("blockReason", out _)) blocked = true;
            if (!root.TryGetProperty("candidates", out var cands) || cands.GetArrayLength() == 0) continue;
            var cand = cands[0];
            if (cand.TryGetProperty("finishReason", out var fr) && fr.ValueKind == JsonValueKind.String) finish = fr.GetString();
            if (!cand.TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts)) continue;
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("thought", out var th) && th.ValueKind == JsonValueKind.True) continue; // thought summaries are never shown or replayed
                rawParts.Add(JsonNode.Parse(part.GetRawText()));
                if (part.TryGetProperty("functionCall", out var fc))
                {
                    var name = fc.GetProperty("name").GetString() ?? "";
                    var id = fc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString()! : $"call_{calls.Count}";
                    var args = fc.TryGetProperty("args", out var a) ? a.GetRawText() : "{}";
                    calls.Add(new AiToolCall(id, name, args));
                }
                else if (part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    var s = t.GetString() ?? "";
                    text.Append(s);
                    if (s.Length > 0) await onText(s);
                }
            }
        }

        var stop = calls.Count > 0 ? AiStop.ToolUse
            : blocked || finish is "SAFETY" or "RECITATION" or "PROHIBITED_CONTENT" or "BLOCKLIST" or "SPII" ? AiStop.Refusal
            : finish == "MAX_TOKENS" ? AiStop.MaxTokens
            : AiStop.Done;
        return new AiResponse(stop, text.ToString(), calls, rawParts.ToJsonString());
    }

    /// <summary>Gemini accepts an OpenAPI subset: drop additionalProperties and empty property bags.</summary>
    private static JsonArray Declarations()
    {
        var arr = new JsonArray();
        foreach (var d in AssistantTools.Definitions())
        {
            var schema = Clean(d!["input_schema"]!.DeepClone());
            var decl = new JsonObject { ["name"] = d["name"]!.GetValue<string>(), ["description"] = d["description"]!.GetValue<string>() };
            if (schema is JsonObject o && o["properties"] is JsonObject { Count: > 0 }) decl["parameters"] = o;
            arr.Add(decl);
        }
        return arr;
    }

    private static JsonNode Clean(JsonNode n)
    {
        if (n is JsonObject o)
        {
            o.Remove("additionalProperties");
            if (o["required"] is JsonArray { Count: 0 }) o.Remove("required");
            foreach (var kv in o.ToList()) if (kv.Value is not null) o[kv.Key] = Clean(kv.Value.DeepClone());
        }
        return n;
    }

    private static JsonArray ToWire(IReadOnlyList<AiMessage> messages)
    {
        var arr = new JsonArray();
        foreach (var m in messages)
        {
            if (m.Results is { Count: > 0 })
            {
                var parts = new JsonArray();
                foreach (var r in m.Results)
                {
                    var payload = JsonNode.Parse(r.Json);
                    var response = new JsonObject { [r.IsError ? "error" : "result"] = payload };
                    parts.Add(new JsonObject { ["functionResponse"] = new JsonObject { ["id"] = r.CallId, ["name"] = r.Name, ["response"] = response } });
                }
                arr.Add(new JsonObject { ["role"] = "user", ["parts"] = parts });
            }
            else if (m.Calls is { Count: > 0 })
            {
                JsonArray parts;
                if (m.Raw is { Length: > 0 }) parts = JsonNode.Parse(m.Raw)!.AsArray();
                else
                {
                    parts = [];
                    foreach (var c in m.Calls)
                        parts.Add(new JsonObject { ["functionCall"] = new JsonObject { ["id"] = c.Id, ["name"] = c.Name, ["args"] = JsonNode.Parse(c.InputJson) } });
                }
                arr.Add(new JsonObject { ["role"] = "model", ["parts"] = parts });
            }
            else arr.Add(new JsonObject { ["role"] = m.Role == "assistant" ? "model" : "user", ["parts"] = new JsonArray(new JsonObject { ["text"] = m.Text }) });
        }
        return arr;
    }

    private static string ExtractError(string body)
    {
        try
        {
            using var d = JsonDocument.Parse(body);
            if (d.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m)) return m.GetString() ?? "error";
        }
        catch (JsonException) { }
        return "error";
    }
}
