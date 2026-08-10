using RagLadder.Api.Configuration;
using RagLadder.Api.Models;

namespace RagLadder.Api.Ask;

public sealed record StageDefinition(
    int Number,
    string Name,
    string Teaches,
    string OptionSummary,
    int[] TrapsFixed);

/// <summary>
/// The ladder (spec §7.2). Presets are cumulative: stage n keeps everything stage n-1 turned on.
/// </summary>
public static class StagePresets
{
    public const int MaxStage = 11;

    public static readonly IReadOnlyList<StageDefinition> Definitions =
    [
        new(0, "No RAG", "Hallucination baseline", "skipRetrieval", []),
        new(1, "Naive RAG", "The core loop", "collection: fixed", []),
        new(2, "Chunking", "Overlap and boundaries", "collection: recursive", [1]),
        new(3, "Metadata filter", "Right title, right year", "useMetadataFilter", [2, 11]),
        new(4, "Hybrid search", "Embeddings can't do exact figures", "useHybrid", [3]),
        new(5, "Reranking", "Retrieve wide, rank precise", "candidateK: 50, useRerank", [4]),
        new(6, "Query rewrite", "Users don't write like press kits", "useQueryRewrite", [5]),
        new(7, "Contextual chunks", "Orphan chunks lack referents", "collection: contextual", [6]),
        new(8, "Citations", "Trust and verification", "+ groundedness", []),
        new(9, "Agentic", "Multi-part needs multi-search", "useAgentic", [7]),
        new(10, "Graph", "Relations, paths, counts", "useGraphExpansion, all modes", [8, 9, 10]),
        new(11, "Router", "Not every query needs every layer", "useRouter", []),
    ];

    public static StageDefinition Definition(int stage) =>
        Definitions.FirstOrDefault(d => d.Number == stage)
        ?? throw new ArgumentOutOfRangeException(nameof(stage), stage, "Stage must be 0..11.");

    /// <summary>Builds the cumulative option set for a stage.</summary>
    public static AskOptions For(int stage, RagLadderOptions cfg)
    {
        if (stage is < 0 or > MaxStage)
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "Stage must be 0..11.");

        var o = new AskOptions
        {
            Collection = ChunkStrategies.Fixed,
            TopK = cfg.Retrieval.TopK,
            CandidateK = cfg.Retrieval.TopK,
            MinEdgeConfidence = cfg.Retrieval.MinEdgeConfidence,
            MaxPathHops = cfg.Retrieval.MaxPathHops,
            IncludeDerivedEdges = true,
        };

        if (stage == 0)
        {
            o.SkipRetrieval = true;
            return o;
        }

        if (stage >= 2) o.Collection = ChunkStrategies.Recursive;
        if (stage >= 3) o.UseMetadataFilter = true;
        if (stage >= 4) o.UseHybrid = true;
        if (stage >= 5) { o.UseRerank = true; o.CandidateK = cfg.Retrieval.CandidateK; }
        if (stage >= 6) o.UseQueryRewrite = true;
        if (stage >= 7) o.Collection = ChunkStrategies.Contextual;
        if (stage >= 8) o.RequireCitations = true;
        if (stage >= 9) o.UseAgentic = true;
        if (stage >= 10)
        {
            o.UseGraphExpansion = true;
            o.GraphHops = new GraphHops { Next = true, Parent = true, Entity = true, EntityRel = true };
        }
        if (stage >= 11) o.UseRouter = true;

        return o;
    }
}
