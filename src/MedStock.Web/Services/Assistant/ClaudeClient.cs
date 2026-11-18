using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MedStock.Web.Services.Assistant;

/// <summary>One content block of a streamed assistant message (text or tool_use).</summary>
public sealed record ClaudeBlock(string Type, string Text, string? Id, string? Name, string InputJson);

public sealed record ClaudeResponse(string StopReason, IReadOnlyList<ClaudeBlock> Blocks);

public sealed class ClaudeApiException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Minimal Messages API client over HttpClient + System.Text.Json (no SDK; the project allows no extra libraries).
/// Sends a streaming request and parses the server-sent events.
/// </summary>
public sealed class ClaudeClient(HttpClient http, IConfiguration cfg)
{
    public const string ApiVersion = "2023-06-01";

    public string? ApiKey => cfg["Claude:ApiKey"];
    public bool Enabled => !string.IsNullOrWhiteSpace(ApiKey);
    private string Endpoint => (cfg["Claude:BaseUrl"] is { Length: > 0 } b ? b.TrimEnd('/') : "https://api.anthropic.com") + "/v1/messages";

    public async Task<ClaudeResponse> StreamAsync(JsonObject body, Func<string, Task> onText, CancellationToken ct)
    {
        if (!Enabled) throw new InvalidOperationException("Claude API key is not configured.");
        body["stream"] = true;
        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("x-api-key", ApiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            throw new ClaudeApiException((int)res.StatusCode, ExtractError(text));
        }

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
                    throw new ClaudeApiException(529, root.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m) ? m.GetString() ?? "stream error" : "stream error");
            }
        }
        return new ClaudeResponse(stop, blocks.Values.Select(b => new ClaudeBlock(b.Type, b.Text.ToString(), b.Id, b.Name, b.Json.ToString())).ToList());
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
