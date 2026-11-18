using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MedStock.Web.Services.Assistant;

/// <summary>
/// Claude via the Messages API over HttpClient + System.Text.Json (no SDK; the project allows no extra libraries).
/// Streams server-sent events, uses prompt caching for tools + system prompt.
/// </summary>
public sealed class AnthropicProvider(HttpClient http, IConfiguration cfg) : IAiProvider
{
    public const string ApiVersion = "2023-06-01";

    public string Name => "Anthropic";
    public string? ApiKey => cfg["Claude:ApiKey"];
    public bool Enabled => !string.IsNullOrWhiteSpace(ApiKey);
    public string Model => cfg["Claude:Model"] is { Length: > 0 } m ? m : "claude-haiku-5-5";
    private string Endpoint => (cfg["Claude:BaseUrl"] is { Length: > 0 } b ? b.TrimEnd('/') : "https://api.anthropic.com") + "/v1/messages";

    public async Task<AiResponse> CompleteAsync(string system, IReadOnlyList<AiMessage> messages, bool allowTools, Func<string, Task> onText, CancellationToken ct)
    {
        if (!Enabled) throw new InvalidOperationException("Claude API key is not configured.");

        // Prompt caching: tools render first, then system; breakpoints on the last tool and on the system block.
        var tools = AssistantTools.Definitions();
        tools[^1]!["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
        var body = new JsonObject
        {
            ["model"] = Model,
            ["max_tokens"] = 1500,
            ["stream"] = true,
            ["system"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = system, ["cache_control"] = new JsonObject { ["type"] = "ephemeral" } }),
            ["tools"] = tools,
            ["thinking"] = new JsonObject { ["type"] = "disabled" },
            ["messages"] = ToWire(messages)
        };
        if (!allowTools) body["tool_choice"] = new JsonObject { ["type"] = "none" };

        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("x-api-key", ApiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
            throw new AiProviderException((int)res.StatusCode, ExtractError(await res.Content.ReadAsStringAsync(ct)));

        var blocks = new SortedDictionary<int, (string Type, StringBuilder Text, string? Id, string? Name, StringBuilder Json)>();
        var stop = "end_turn";
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data.Length == 0) continue;
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "content_block_start":
                {
                    var idx = root.GetProperty("index").GetInt32();
                    var cb = root.GetProperty("content_block");
                    var type = cb.GetProperty("type").GetString() ?? "";
                    var sb = new StringBuilder();
                    if (type == "text" && cb.TryGetProperty("text", out var t0)) sb.Append(t0.GetString());
                    blocks[idx] = (type, sb, cb.TryGetProperty("id", out var id) ? id.GetString() : null,
                        cb.TryGetProperty("name", out var nm) ? nm.GetString() : null, new StringBuilder());
                    break;
                }
                case "content_block_delta":
                {
                    var idx = root.GetProperty("index").GetInt32();
                    var delta = root.GetProperty("delta");
                    if (!blocks.TryGetValue(idx, out var b)) break;
                    switch (delta.GetProperty("type").GetString())
                    {
                        case "text_delta":
                            var s = delta.GetProperty("text").GetString() ?? "";
                            b.Text.Append(s);
                            if (s.Length > 0) await onText(s);
                            break;
                        case "input_json_delta":
                            b.Json.Append(delta.GetProperty("partial_json").GetString());
                            break;
                    }
                    break;
                }
                case "message_delta":
                    if (root.GetProperty("delta").TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String) stop = sr.GetString()!;
                    break;
                case "error":
                    throw new AiProviderException(529, root.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m) ? m.GetString() ?? "stream error" : "stream error");
            }
        }

        var text = string.Concat(blocks.Values.Where(b => b.Type == "text").Select(b => b.Text.ToString()));
        var calls = blocks.Values.Where(b => b.Type == "tool_use").Select(b => new AiToolCall(b.Id ?? "", b.Name ?? "", b.Json.ToString())).ToList();
        var kind = stop switch { "tool_use" => AiStop.ToolUse, "max_tokens" => AiStop.MaxTokens, "refusal" => AiStop.Refusal, _ => AiStop.Done };
        return new AiResponse(kind, text, calls);
    }

    private static JsonArray ToWire(IReadOnlyList<AiMessage> messages)
    {
        var arr = new JsonArray();
        foreach (var m in messages)
        {
            if (m.Results is { Count: > 0 })
            {
                var content = new JsonArray();
                foreach (var r in m.Results)
                {
                    var tr = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = r.CallId, ["content"] = r.Json };
                    if (r.IsError) tr["is_error"] = true;
                    content.Add(tr);
                }
                arr.Add(new JsonObject { ["role"] = "user", ["content"] = content });
            }
            else if (m.Calls is { Count: > 0 })
            {
                var content = new JsonArray();
                if (m.Text.Length > 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = m.Text });
                foreach (var c in m.Calls)
                    content.Add(new JsonObject { ["type"] = "tool_use", ["id"] = c.Id, ["name"] = c.Name, ["input"] = JsonNode.Parse(string.IsNullOrWhiteSpace(c.InputJson) ? "{}" : c.InputJson) });
                arr.Add(new JsonObject { ["role"] = "assistant", ["content"] = content });
            }
            else arr.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Text });
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
