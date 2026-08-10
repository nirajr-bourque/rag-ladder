using System.Diagnostics;
using Microsoft.Extensions.Options;
using RagLadder.Api.Configuration;
using RagLadder.Api.Embedding;
using RagLadder.Api.Models;
using RagLadder.Api.Reranking;
using RagLadder.Api.Vector;

namespace RagLadder.Api.Ask;

public sealed record RetrievalOutcome(
    IReadOnlyList<RetrievedChunk> Selected,
    IReadOnlyList<RetrievedChunk> Candidates,
    int DroppedCount,
    long EmbedMs,
    long SearchMs,
    long RerankMs);

/// <summary>
/// Retrieval for one rung of the ladder. Each capability is switchable independently so the same
/// question can be run at stage 4 and stage 5 and compared side by side.
/// </summary>
public sealed class Retriever(
    IVectorStore vectors,
    IEmbedder embedder,
    IReranker reranker,
    IOptions<RagLadderOptions> options)
{
    private readonly RetrievalOptions _config = options.Value.Retrieval;

    public async Task<RetrievalOutcome> RetrieveAsync(
        string docId, string searchText, AskOptions askOptions, CancellationToken ct)
    {
        var collection = CollectionNames.For(docId, askOptions.Collection);
        var filter = askOptions.UseMetadataFilter ? askOptions.Filter : null;
        var wide = Math.Max(askOptions.TopK, askOptions.UseRerank ? askOptions.CandidateK : askOptions.TopK);

        var embedWatch = Stopwatch.StartNew();
        var queryVector = (await embedder.EmbedAsync([searchText], ct))[0];
        embedWatch.Stop();

        var searchWatch = Stopwatch.StartNew();
        var vectorHits = await vectors.SearchAsync(collection, queryVector, wide, filter, ct);

        List<RetrievedChunk> candidates;
        if (askOptions.UseHybrid)
        {
            var keywordHits = await vectors.KeywordSearchAsync(collection, searchText, wide, filter, ct);
            var payloads = vectorHits.Concat(keywordHits)
                .GroupBy(h => h.ChunkId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Payload, StringComparer.Ordinal);

            var fused = Rrf.Fuse(vectorHits, keywordHits, _config.RrfK);
            candidates = [.. fused.Take(wide).Select((f, i) => Map(payloads[f.Id], f.Score, f.Arm, i + 1, f.VectorScore, f.KeywordScore))];
        }
        else
        {
            candidates = [.. vectorHits.Select((h, i) => Map(h.Payload, h.Score, "vector", i + 1, h.Score, null))];
        }
        searchWatch.Stop();

        var rerankWatch = Stopwatch.StartNew();
        List<RetrievedChunk> selected;
        if (askOptions.UseRerank && candidates.Count > 0)
        {
            var scores = await reranker.ScoreAsync(searchText, [.. candidates.Select(c => c.Text)], ct);
            var ranked = candidates
                .Select((c, i) => c with { RerankScore = scores[i] })
                .OrderByDescending(c => c.RerankScore)
                .ToList();
            selected = [.. ranked.Take(askOptions.TopK).Select((c, i) => c with { RankAfter = i + 1, Score = c.RerankScore ?? c.Score })];
            candidates = [.. ranked.Select((c, i) => c with { RankAfter = i + 1 })];
        }
        else
        {
            selected = [.. candidates.Take(askOptions.TopK).Select((c, i) => c with { RankAfter = i + 1 })];
        }
        rerankWatch.Stop();

        return new RetrievalOutcome(
            selected,
            candidates,
            Math.Max(0, candidates.Count - selected.Count),
            embedWatch.ElapsedMilliseconds,
            searchWatch.ElapsedMilliseconds,
            rerankWatch.ElapsedMilliseconds);
    }

    private static RetrievedChunk Map(ChunkPayload p, double score, string arm, int rankBefore, double? vectorScore, double? keywordScore) => new()
    {
        ChunkId = p.ChunkId,
        Text = p.Text,
        Page = p.Page,
        Section = p.Section,
        DocType = p.DocType,
        Subject = p.Subject,
        Year = p.Year,
        Score = score,
        VectorScore = vectorScore,
        KeywordScore = keywordScore,
        Arm = arm,
        RankBefore = rankBefore,
    };
}
