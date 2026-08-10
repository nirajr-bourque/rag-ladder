using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RagLadder.Api.Configuration;
using RagLadder.Api.Llm;
using RagLadder.Api.Models;

namespace RagLadder.Api.Ask;

public sealed record GeneratedAnswer(
    string Answer,
    bool Refused,
    string Prompt,
    long ElapsedMs,
    int ChatCalls,
    string? Warning);

/// <summary>
/// Builds the prompt and enforces flow isolation. Two rules matter more here than anywhere else:
/// the model answers only from the supplied context, and when the context is insufficient it must
/// reply with the exact refusal string. A model asked about films will otherwise answer happily
/// from its training data, and an ungrounded correct-sounding answer would silently destroy the
/// demo (spec §7.4).
/// </summary>
public sealed class AnswerGenerator(IChatClient chat, IOptions<RagLadderOptions> options)
{
    private readonly RetrievalOptions _config = options.Value.Retrieval;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string RefusalText => _config.RefusalText;

    /// <summary>Stage 0: no retrieval, no constraint. Deliberately allowed to hallucinate.</summary>
    public async Task<GeneratedAnswer> UnconstrainedAsync(string question, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(
                "Answer the user's question about film and television from your own knowledge. " +
                "Be specific and give names, titles and figures. Do not mention that you lack sources."),
            ChatMessage.User(question)
        };

        var response = await chat.CompleteAsync(new ChatRequest
        {
            Model = chat.ChatModel,
            Messages = messages,
            Temperature = 0,
            Purpose = ChatPurpose.Unconstrained,
            CacheScope = "stage0",
        }, ct);

        return new GeneratedAnswer(
            response.Failed ? "" : response.Content.Trim(),
            false,
            Render(messages),
            response.ElapsedMs,
            response.FromCache ? 0 : 1,
            response.Warning);
    }

    public async Task<GeneratedAnswer> AnswerAsync(
        string question,
        IReadOnlyList<RetrievedChunk> context,
        GraphBlock? graph,
        AskOptions askOptions,
        string cacheScope,
        CancellationToken ct)
    {
        var system = new StringBuilder();
        system.AppendLine("You answer questions about a film and television document collection.");
        system.AppendLine();
        system.AppendLine("ABSOLUTE RULES");
        system.AppendLine("1. Answer ONLY from the CONTEXT below. You have no other knowledge of this material.");
        system.AppendLine("2. The context describes a fictional universe. Anything you believe you know about");
        system.AppendLine("   real films, real studios or real people is irrelevant and must not be used.");
        system.AppendLine($"3. If the context does not contain the answer, reply with exactly this and nothing else:");
        system.AppendLine($"   {_config.RefusalText}");
        system.AppendLine("4. Never guess, never hedge into a partial answer, never say what is 'likely'.");
        if (askOptions.RequireCitations)
        {
            system.AppendLine("5. Cite every claim with the bracketed chunk marker it came from, e.g. [1]. Every");
            system.AppendLine("   sentence containing a fact must carry at least one marker.");
        }

        var user = new StringBuilder();
        user.AppendLine("CONTEXT");
        if (context.Count == 0 && graph?.Path is null && graph?.AggregationResult is null)
        {
            user.AppendLine("(no context was retrieved)");
        }
        else
        {
            for (var i = 0; i < context.Count; i++)
            {
                var c = context[i];
                user.AppendLine($"[{i + 1}] (chunk {c.ChunkId}, page {c.Page}, section \"{c.Section}\")");
                user.AppendLine(c.Text.Trim());
                user.AppendLine();
            }
            AppendGraph(user, graph);
        }

        user.AppendLine("QUESTION");
        user.AppendLine(question);

        var messages = new List<ChatMessage> { ChatMessage.System(system.ToString()), ChatMessage.User(user.ToString()) };

        var response = await chat.CompleteAsync(new ChatRequest
        {
            Model = chat.ChatModel,
            Messages = messages,
            Temperature = 0,
            Purpose = ChatPurpose.Answer,
            CacheScope = cacheScope,
        }, ct);

        if (response.Failed)
        {
            // A rate-limited chat call must still return retrieval results — the retrieval half is
            // the interesting half (spec §12).
            return new GeneratedAnswer("", false, Render(messages), response.ElapsedMs, 0,
                $"Answer generation failed: {response.Warning}");
        }

        var answer = response.Content.Trim();
        var refused = IsRefusal(answer);
        return new GeneratedAnswer(answer, refused, Render(messages), response.ElapsedMs, response.FromCache ? 0 : 1, null);
    }

    private static void AppendGraph(StringBuilder user, GraphBlock? graph)
    {
        if (graph is null) return;

        if (graph.Path is { } path && path.Nodes.Count > 0)
        {
            user.AppendLine("GRAPH PATH — computed by traversal, not retrieved from text.");
            user.AppendLine($"  {string.Join(" -> ", path.Nodes.Select(n => $"{n.Name} [{n.Type}]"))}");
            user.AppendLine($"  relations: {string.Join(" | ", path.Rels)}  ({path.Hops} hops)");
            user.AppendLine($"  reading: {path.Narrative}");
            user.AppendLine("  State this connection as fact; the graph already computed it. Your job is only to phrase it.");
            user.AppendLine();
        }

        if (graph.AggregationResult is { } agg && agg.Rows.Count > 0)
        {
            user.AppendLine($"GRAPH AGGREGATION — {agg.Title}. Counted over the whole graph, not a sample.");
            user.AppendLine("  " + string.Join(" | ", agg.Columns));
            foreach (var row in agg.Rows.Take(15))
                user.AppendLine("  " + string.Join(" | ", agg.Columns.Select(c => row.Values.GetValueOrDefault(c)?.ToString() ?? "")));
            user.AppendLine();
        }

        if (graph.EdgesTraversed.Count > 0)
        {
            user.AppendLine("GRAPH RELATIONS reachable from the retrieved chunks:");
            foreach (var e in graph.EdgesTraversed.Take(30))
                user.AppendLine($"  {e.FromName} -{e.Predicate}-> {e.ToName}" +
                                $" (confidence {e.Confidence:F2}, {e.MentionCount} mention(s){(e.Derived ? ", derived" : "")})");
            user.AppendLine();
        }
    }

    public bool IsRefusal(string answer)
    {
        var normalized = answer.Trim().TrimEnd('.').Trim();
        var expected = _config.RefusalText.Trim().TrimEnd('.').Trim();
        return normalized.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string Render(IEnumerable<ChatMessage> messages) =>
        string.Join("\n\n", messages.Select(m => $"### {m.Role}\n{m.Content}"));

    /// <summary>
    /// The cache key covers the document, the question and every resolved flag, so no two rungs
    /// can ever share a completion (spec §7.4).
    /// </summary>
    public static string CacheScopeFor(string docId, string question, AskOptions options) =>
        Hashing.Sha256Hex(docId + "|" + question + "|" + JsonSerializer.Serialize(options, Json))[..24];
}
