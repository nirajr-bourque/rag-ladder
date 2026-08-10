using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RagLadder.Api.Configuration;
using RagLadder.Api.Llm;
using RagLadder.Api.Models;

namespace RagLadder.Api.Ask;

public sealed record AgenticOutcome(
    IReadOnlyList<RetrievedChunk> Chunks,
    IReadOnlyList<AgenticStep> Trace,
    int Calls,
    long ElapsedMs,
    string? Warning);

/// <summary>
/// Stage 9. One question can need more than one search: "compare the opening weekends of A and B"
/// cannot be served by a single top-k. The loop is hard-bounded — four iterations, six chat calls
/// — and on hitting a cap it returns a partial answer with a warning rather than looping (spec §7.5).
/// </summary>
public sealed class AgenticLoop(IChatClient chat, Retriever retriever, IOptions<RagLadderOptions> options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly AgenticOptions _config = options.Value.Agentic;

    public async Task<AgenticOutcome> RunAsync(
        string docId, string question, AskOptions askOptions, CancellationToken ct)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var trace = new List<AgenticStep>();
        var collected = new Dictionary<string, RetrievedChunk>(StringComparer.Ordinal);
        var calls = 0;
        string? warning = null;
        var history = new StringBuilder();

        for (var iteration = 1; iteration <= _config.MaxIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();

            if (calls >= _config.MaxChatCalls)
            {
                warning = $"Agentic loop stopped at the {_config.MaxChatCalls}-call cap. The answer is based on {collected.Count} chunk(s) gathered so far.";
                break;
            }

            var response = await chat.CompleteAsync(new ChatRequest
            {
                Model = chat.ChatModel,
                Messages =
                [
                    ChatMessage.System(SystemPrompt()),
                    ChatMessage.User($"QUESTION: {question}\n\nSEARCHES SO FAR:\n{(history.Length == 0 ? "  (none)" : history.ToString())}")
                ],
                Temperature = 0,
                JsonOnly = true,
                Purpose = ChatPurpose.Agentic,
                CacheScope = $"iter{iteration}",
            }, ct);

            if (!response.FromCache) calls++;

            if (response.Failed)
            {
                warning = $"Agentic planning call failed: {response.Warning}. Falling back to a single search.";
                break;
            }

            var plan = JsonText.TryDeserialize<AgentPlan>(response.Content, Json);
            if (plan is null)
            {
                warning = "Agentic planner returned malformed JSON; falling back to a single search.";
                break;
            }

            if (string.Equals(plan.Action, "answer", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(plan.Query))
            {
                trace.Add(new AgenticStep
                {
                    Iteration = iteration, Action = "answer", Thought = plan.Thought, Hits = collected.Count
                });
                break;
            }

            var filter = plan.Filter is null ? new ChunkFilter() : plan.Filter;
            var searchOptions = askOptions.Clone();
            searchOptions.UseAgentic = false;
            searchOptions.UseGraphExpansion = false;
            if (!filter.IsEmpty) { searchOptions.UseMetadataFilter = true; searchOptions.Filter = filter; }

            var outcome = await retriever.RetrieveAsync(docId, plan.Query!, searchOptions, ct);
            foreach (var chunk in outcome.Selected) collected.TryAdd(chunk.ChunkId, chunk);

            trace.Add(new AgenticStep
            {
                Iteration = iteration,
                Action = "search",
                Query = plan.Query,
                Filter = filter.IsEmpty ? null : filter,
                Hits = outcome.Selected.Count,
                Thought = plan.Thought,
                ChunkIds = [.. outcome.Selected.Select(c => c.ChunkId)],
            });

            history.AppendLine($"  [{iteration}] search \"{plan.Query}\" -> {outcome.Selected.Count} hits");
            foreach (var chunk in outcome.Selected.Take(3))
                history.AppendLine($"        {Truncate(chunk.Text, 220)}");

            if (iteration == _config.MaxIterations)
                warning ??= $"Agentic loop stopped at the {_config.MaxIterations}-iteration cap. The answer may be partial.";
        }

        if (collected.Count == 0)
        {
            // Never return nothing: fall back to a plain search so the rung still produces an answer.
            var fallbackOptions = askOptions.Clone();
            fallbackOptions.UseAgentic = false;
            fallbackOptions.UseGraphExpansion = false;
            var outcome = await retriever.RetrieveAsync(docId, question, fallbackOptions, ct);
            foreach (var chunk in outcome.Selected) collected.TryAdd(chunk.ChunkId, chunk);
            trace.Add(new AgenticStep
            {
                Iteration = trace.Count + 1, Action = "fallback-search", Query = question,
                Hits = outcome.Selected.Count, ChunkIds = [.. outcome.Selected.Select(c => c.ChunkId)]
            });
        }

        return new AgenticOutcome([.. collected.Values], trace, calls, watch.ElapsedMilliseconds, warning);
    }

    private string SystemPrompt() => $$"""
        You plan retrieval for a question about a film and television document collection. You have
        exactly one tool: search(query, filter).

        Decide whether another search would help. A multi-part question — comparing two films,
        asking about two people, or asking for a figure plus a credit — needs one search per part.
        A single-fact question needs one search and then you are done.

        Filters available: docType, year, yearRange [from, to], studio, subject.

        Reply with JSON only, one of:
          {"action":"search","query":"...","filter":{"docType":null,"year":null},"thought":"why"}
          {"action":"answer","thought":"why no further search is needed"}

        You have at most {{_config.MaxIterations}} iterations. Do not repeat a search you have already run.
        """;

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text.ReplaceLineEndings(" ") : text[..max].ReplaceLineEndings(" ") + "…";

    private sealed class AgentPlan
    {
        public string? Action { get; set; }
        public string? Query { get; set; }
        public ChunkFilter? Filter { get; set; }
        public string? Thought { get; set; }
    }
}
