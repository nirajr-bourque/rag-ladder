using System.Text.Json;
using RagLadder.Api.Graph;
using RagLadder.Api.Infrastructure;
using RagLadder.Api.Llm;
using RagLadder.Api.Models;

namespace RagLadder.Api.Ask;

public sealed record GraphStageOutcome(GraphBlock Block, IReadOnlyList<RetrievedChunk> ExtraChunks, int Calls, string? Warning);

/// <summary>
/// Stage 10 in its three modes (spec §7.3).
///
/// <c>expand</c> seeds from vector search and walks the graph. <c>path</c> answers the six-degrees
/// question that no amount of top-k can reach. <c>aggregate</c> skips vector search entirely and
/// counts over the whole graph.
/// </summary>
public sealed class GraphStageService(
    IGraphStore graph,
    CorpusRepository corpus,
    IChatClient chat,
    Microsoft.Extensions.Options.IOptions<Configuration.RagLadderOptions> options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _extractionStrategy = options.Value.Extraction.SourceStrategy;

    public async Task<GraphStageOutcome> RunAsync(
        string docId,
        string question,
        AskOptions askOptions,
        IReadOnlyList<RetrievedChunk> seeds,
        CancellationToken ct)
    {
        return askOptions.GraphMode switch
        {
            GraphModes.Path => await PathAsync(docId, question, askOptions, ct),
            GraphModes.Aggregate => await AggregateAsync(docId, question, askOptions, ct),
            _ => await ExpandAsync(docId, askOptions, seeds, ct)
        };
    }

    // ----- expand ---------------------------------------------------------

    private async Task<GraphStageOutcome> ExpandAsync(
        string docId, AskOptions askOptions, IReadOnlyList<RetrievedChunk> seeds, CancellationToken ct)
    {
        var seedIds = seeds.Select(s => s.ChunkId).ToList();

        // Seeds may come from any collection; the graph only holds chunks from the strategy the
        // extraction ran on, so map across by character-span overlap first.
        var mapped = corpus.MapToStrategy(docId, seedIds, _extractionStrategy);

        var expansion = await graph.ExpandAsync(
            docId, mapped, askOptions.GraphHops, askOptions.MinEdgeConfidence, askOptions.IncludeDerivedEdges, ct);

        var extra = new List<RetrievedChunk>();
        var seen = seedIds.ToHashSet(StringComparer.Ordinal);

        foreach (var chunk in expansion.Chunks)
        {
            if (askOptions.GraphHops.Next)
            {
                AddNeighbour(chunk.PrevId, chunk.PrevText, "previous chunk (:NEXT)");
                AddNeighbour(chunk.NextId, chunk.NextText, "next chunk (:NEXT)");
            }
            if (askOptions.GraphHops.EntityRel)
            {
                foreach (var related in chunk.RelatedChunkIds.Take(3))
                {
                    if (!seen.Add(related)) continue;
                    var record = corpus.GetChunk(related);
                    if (record is null) continue;
                    extra.Add(FromRecord(record, "reached through a shared entity"));
                }
            }
            continue;

            void AddNeighbour(string? id, string? text, string reason)
            {
                if (id is null || text is null || !seen.Add(id)) return;
                var record = corpus.GetChunk(id);
                extra.Add(record is null
                    ? new RetrievedChunk { ChunkId = id, Text = text, Page = 0, Section = "", Arm = "graph", FromGraph = true, GraphReason = reason }
                    : FromRecord(record, reason));
            }
        }

        var block = new GraphBlock
        {
            Mode = GraphModes.Expand,
            SeedChunkIds = seedIds,
            EntitiesTouched = expansion.EntitiesTouched,
            EdgesTraversed = expansion.EdgesTraversed,
            Note = mapped.Count == 0
                ? "No graph chunks matched the retrieved seeds. Has the graph been committed for this document?"
                : null,
        };
        return new GraphStageOutcome(block, extra, 0, null);
    }

    private static RetrievedChunk FromRecord(ChunkRecord record, string reason) => new()
    {
        ChunkId = record.Id,
        Text = record.RawText,
        Page = record.Page,
        Section = record.SectionId,
        DocType = record.FrontMatter.DocType,
        Subject = record.FrontMatter.Subject,
        Year = record.FrontMatter.Year,
        Arm = "graph",
        FromGraph = true,
        GraphReason = reason,
    };

    // ----- path -----------------------------------------------------------

    private async Task<GraphStageOutcome> PathAsync(
        string docId, string question, AskOptions askOptions, CancellationToken ct)
    {
        var calls = 0;
        var from = askOptions.PathFrom;
        var to = askOptions.PathTo;
        string? warning = null;

        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
        {
            var (resolvedFrom, resolvedTo, usedCall, resolveWarning) = await ResolveEndpointsAsync(docId, question, ct);
            calls += usedCall;
            from ??= resolvedFrom;
            to ??= resolvedTo;
            warning = resolveWarning;
        }

        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
            return new GraphStageOutcome(
                new GraphBlock { Mode = GraphModes.Path, Note = "Could not identify two people to connect from the question." },
                [], calls, warning);

        var path = await graph.ShortestPathAsync(docId, from, to,
            Math.Clamp(askOptions.MaxPathHops, 1, 20), askOptions.MinEdgeConfidence, ct);

        return new GraphStageOutcome(new GraphBlock
        {
            Mode = GraphModes.Path,
            Path = path,
            EntitiesTouched = path is null ? [] : [.. path.Nodes.Select(n => new GraphEntity { Key = n.Key, Name = n.Name, Type = n.Type, Year = n.Year })],
            Note = path is null
                ? $"No path of {askOptions.MaxPathHops} hops or fewer connects these two through credited work."
                : null,
        }, [], calls, warning);
    }

    /// <summary>Maps two names in the question onto entity keys, preferring an exact graph match.</summary>
    private async Task<(string? From, string? To, int Calls, string? Warning)> ResolveEndpointsAsync(
        string docId, string question, CancellationToken ct)
    {
        var people = await graph.SearchEntitiesAsync(docId, "Person", null, 500, ct);
        if (people.Count == 0) return (null, null, 0, "The graph holds no Person nodes yet.");

        // A direct name match beats asking the model, and costs nothing.
        var direct = people
            .Where(p => question.Contains(p.Name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Name.Length)
            .ToList();
        if (direct.Count >= 2) return (direct[0].Key, direct[1].Key, 0, null);

        var response = await chat.CompleteAsync(new ChatRequest
        {
            Model = chat.ChatModel,
            Messages =
            [
                ChatMessage.System(
                    "Identify the two people the question asks to connect. Choose only from the supplied list, " +
                    "copying the names exactly. Return JSON only: {\"from\":\"...\",\"to\":\"...\"}"),
                ChatMessage.User($"QUESTION: {question}\n\nPEOPLE IN THE GRAPH:\n" +
                                 string.Join('\n', people.Take(200).Select(p => "  " + p.Name)))
            ],
            Temperature = 0,
            JsonOnly = true,
            Purpose = ChatPurpose.PathEndpoints,
        }, ct);

        if (response.Failed)
            return (direct.FirstOrDefault()?.Key, null, 0, $"Could not resolve path endpoints: {response.Warning}");

        var parsed = JsonText.TryDeserialize<Endpoints>(response.Content, Json);
        string? Match(string? name) => name is null
            ? null
            : people.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Key
              ?? people.FirstOrDefault(p => p.Name.Contains(name, StringComparison.OrdinalIgnoreCase))?.Key;

        return (Match(parsed?.From), Match(parsed?.To), response.FromCache ? 0 : 1, null);
    }

    private sealed class Endpoints
    {
        public string? From { get; set; }
        public string? To { get; set; }
    }

    // ----- aggregate ------------------------------------------------------

    private async Task<GraphStageOutcome> AggregateAsync(
        string docId, string question, AskOptions askOptions, CancellationToken ct)
    {
        var preset = askOptions.AggregationPreset ?? GuessPreset(question);
        var year = askOptions.AggregationYear ?? GuessYear(question);

        var result = await graph.AggregateAsync(docId, preset, year, askOptions.MinEdgeConfidence, ct);
        return new GraphStageOutcome(new GraphBlock
        {
            Mode = GraphModes.Aggregate,
            AggregationResult = result,
            Note = result.Rows.Count == 0 ? "The aggregation returned no rows — the graph may be empty or the confidence floor too high." : null,
        }, [], 0, null);
    }

    public static string GuessPreset(string question)
    {
        var q = question.ToLowerInvariant();
        if (q.Contains("franchise")) return AggregationPresets.MultiFranchiseActors;
        if (q.Contains("award") || q.Contains("won") || q.Contains("wins")) return AggregationPresets.AwardTallyByStudio;
        if (q.Contains("cinematograph") || q.Contains("shot") || q.Contains("photography")) return AggregationPresets.DirectorCinematographerPairs;
        return AggregationPresets.StudioFilmCount;
    }

    private static int? GuessYear(string question)
    {
        for (var i = 0; i + 4 <= question.Length; i++)
        {
            var slice = question.AsSpan(i, 4);
            if (int.TryParse(slice, out var year) && year is >= 1900 and <= 2100)
            {
                var before = i == 0 || !char.IsLetterOrDigit(question[i - 1]);
                var after = i + 4 >= question.Length || !char.IsLetterOrDigit(question[i + 4]);
                if (before && after) return year;
            }
        }
        return null;
    }
}
