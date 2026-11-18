namespace MedStock.Web.Services.Assistant;

public sealed record AiToolCall(string Id, string Name, string InputJson);
public sealed record AiToolResult(string CallId, string Name, string Json, bool IsError);

/// <summary>Provider-neutral conversation message: plain text, an assistant turn with tool calls, or a user turn carrying tool results.</summary>
public sealed record AiMessage(string Role, string Text, IReadOnlyList<AiToolCall>? Calls = null, IReadOnlyList<AiToolResult>? Results = null, string? Raw = null);

public enum AiStop { Done, ToolUse, MaxTokens, Refusal }
/// <summary>Raw is an opaque provider payload (e.g. Gemini parts with thought signatures) that is replayed verbatim with the assistant turn.</summary>
public sealed record AiResponse(AiStop Stop, string Text, IReadOnlyList<AiToolCall> Calls, string? Raw = null);

public sealed class AiProviderException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>One model backend. The assistant loop, tools and UI only know this interface; select the implementation with config Ai:Provider.</summary>
public interface IAiProvider
{
    string Name { get; }
    string Model { get; }
    bool Enabled { get; }

    /// <summary>One streamed model turn. Text deltas go to <paramref name="onText"/>; <paramref name="allowTools"/>=false forbids tool calls.</summary>
    Task<AiResponse> CompleteAsync(string system, IReadOnlyList<AiMessage> messages, bool allowTools, Func<string, Task> onText, CancellationToken ct);
}

/// <summary>Selected when Ai:Provider names an implementation that does not exist: everything shows the disabled state.</summary>
public sealed class DisabledProvider(string name) : IAiProvider
{
    public string Name => name;
    public string Model => "";
    public bool Enabled => false;
    public Task<AiResponse> CompleteAsync(string system, IReadOnlyList<AiMessage> messages, bool allowTools, Func<string, Task> onText, CancellationToken ct) =>
        throw new InvalidOperationException("AI provider is not available.");
}

public static class AiProviderSelector
{
    /// <summary>Ai:Provider = "Gemini" (default, free tier) or "Anthropic". Add new providers here; nothing else needs to change.</summary>
    public static IAiProvider Create(IConfiguration cfg, HttpClient http)
    {
        var name = cfg["Ai:Provider"];
        if (string.IsNullOrWhiteSpace(name) || name.Equals("Gemini", StringComparison.OrdinalIgnoreCase)) return new GeminiProvider(http, cfg);
        if (name.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)) return new AnthropicProvider(http, cfg);
        return new DisabledProvider(name);
    }
}
