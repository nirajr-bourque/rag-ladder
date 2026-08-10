using System.Text;
using System.Text.Json;

namespace RagLadder.Api.Llm;

public sealed record ChatMessage(string Role, string Content)
{
    public static ChatMessage System(string content) => new("system", content);
    public static ChatMessage User(string content) => new("user", content);
    public static ChatMessage Assistant(string content) => new("assistant", content);
}

public static class ChatPurpose
{
    public const string Answer = "answer";
    public const string Unconstrained = "unconstrained";
    public const string SectionSummary = "section-summary";
    public const string Extraction = "extraction";
    public const string Verification = "verification";
    public const string QueryRewrite = "query-rewrite";
    public const string Rerank = "rerank";
    public const string Agentic = "agentic";
    public const string Routing = "routing";
    public const string PathEndpoints = "path-endpoints";
    public const string PathNarrative = "path-narrative";
    public const string GoldenGeneration = "golden-generation";

    /// <summary>
    /// Bulk work runs in the background during processing and can be dozens of calls deep.
    /// Everything else is a person waiting on a screen. They queue separately so a question asked
    /// mid-processing waits for one call rather than the whole extraction backlog.
    /// </summary>
    public static bool IsBulk(string purpose) =>
        purpose is Extraction or Verification or SectionSummary or GoldenGeneration;
}

public sealed record ChatRequest
{
    public required string Model { get; init; }
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    public double Temperature { get; init; }
    public bool JsonOnly { get; init; }
    public required string Purpose { get; init; }
    /// <summary>
    /// Extra discriminator folded into the cache key. Answer generation passes the resolved
    /// stage flag signature so two stages can never share a cached completion (spec §7.4).
    /// </summary>
    public string CacheScope { get; init; } = "";
    public bool BypassCache { get; init; }
}

public sealed record ChatResult
{
    public required string Content { get; init; }
    public required string Model { get; init; }
    public bool FromCache { get; init; }
    public long ElapsedMs { get; init; }
    public int? PromptTokens { get; init; }
    public int? CompletionTokens { get; init; }
    /// <summary>Set when the call failed and a degraded result is being returned instead.</summary>
    public string? Warning { get; init; }
    public bool Failed { get; init; }
}

public interface IChatClient
{
    string Kind { get; }
    string ChatModel { get; }
    string ExtractionModel { get; }
    /// <summary>Live model calls made since process start (cache hits excluded).</summary>
    int LiveCallCount { get; }
    Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct = default);
    Task<ProviderHealth> HealthAsync(CancellationToken ct = default);
}

public sealed record ProviderHealth(string Name, string Status, string Detail)
{
    public const string Ok = "ok";
    public const string Degraded = "degraded";
    public const string Paused = "paused";
    public const string Unreachable = "unreachable";
    public const string NotConfigured = "not-configured";

    public bool Healthy => Status == Ok;
}

/// <summary>Tolerant JSON extraction — models wrap objects in prose or code fences often enough to matter.</summary>
public static class JsonText
{
    public static string? ExtractObject(string content) => Extract(content, '{', '}');
    public static string? ExtractArray(string content) => Extract(content, '[', ']');

    private static string? Extract(string content, char open, char close)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        var text = content.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline > 0) text = text[(firstNewline + 1)..];
            var fence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0) text = text[..fence];
            text = text.Trim();
        }

        var start = text.IndexOf(open);
        if (start < 0) return null;

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (escaped) { escaped = false; continue; }
            if (c == '\\' && inString) { escaped = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == open) depth++;
            else if (c == close)
            {
                depth--;
                if (depth == 0) return text[start..(i + 1)];
            }
        }
        return null;
    }

    public static T? TryDeserialize<T>(string content, JsonSerializerOptions options) where T : class
    {
        var json = ExtractObject(content) ?? content;
        try { return JsonSerializer.Deserialize<T>(json, options); }
        catch (JsonException) { return null; }
    }

    public static string Fingerprint(IEnumerable<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var m in messages) sb.Append(m.Role).Append('|').Append(m.Content).Append('~');
        return sb.ToString();
    }
}
