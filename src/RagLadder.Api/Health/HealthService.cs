using Microsoft.Extensions.Options;
using RagLadder.Api.Configuration;
using RagLadder.Api.Embedding;
using RagLadder.Api.Graph;
using RagLadder.Api.Infrastructure;
using RagLadder.Api.Llm;
using RagLadder.Api.Reranking;
using RagLadder.Api.Vector;

namespace RagLadder.Api.Health;

public sealed record EmbedderProbe(bool Ran, double SimilarPair, double UnrelatedPair, bool Passed, string Detail);

public sealed record HealthReport(
    string Status,
    IReadOnlyList<ProviderHealth> Providers,
    EmbedderProbe Embedder,
    CacheStats Caches,
    IReadOnlyDictionary<string, string> Configuration);

/// <summary>
/// Phase 1's acceptance criterion made into an endpoint: health green, and similar sentences
/// scoring above 0.7 cosine while unrelated ones score below 0.3 (spec §11).
/// </summary>
public sealed class HealthService(
    IEmbedder embedder,
    IReranker reranker,
    IVectorStore vectors,
    IGraphStore graph,
    IChatClient chat,
    CacheRepository caches,
    IOptions<RagLadderOptions> options)
{
    private readonly RagLadderOptions _config = options.Value;

    public async Task<HealthReport> CheckAsync(CancellationToken ct)
    {
        var providers = new List<ProviderHealth>
        {
            new("embedder",
                embedder.IsRealModel ? ProviderHealth.Ok : ProviderHealth.Degraded,
                embedder.IsRealModel
                    ? $"{embedder.ModelId}, {embedder.Dimensions} dims, {(_config.Providers.Embedder.Equals("ollama", StringComparison.OrdinalIgnoreCase) ? "served by Ollama" : "ONNX in-process")}."
                    : $"Running the deterministic dev stand-in ({embedder.ModelId}). Retrieval quality is not representative — " +
                      "run tools/fetch-models.ps1, or set Providers:Embedder to \"ollama\" if the model files are unreachable."),
            new("reranker",
                reranker.IsRealModel ? ProviderHealth.Ok : ProviderHealth.Degraded,
                reranker switch
                {
                    LlmReranker => "Chat model scoring (query and passage judged together). No local file needed; costs one call per batch.",
                    { IsRealModel: true } => $"{reranker.ModelId}, cross-encoder, ONNX in-process.",
                    _ => $"Running the lexical dev stand-in ({reranker.ModelId}). The stage-5 rank jump will be muted — " +
                         "run tools/fetch-models.ps1, or set Providers:Reranker to \"llm\" if the model files are unreachable."
                }),
        };

        providers.Add(await Safe(() => vectors.HealthAsync(ct), "vector"));
        providers.Add(await Safe(() => graph.HealthAsync(ct), "graph"));
        providers.Add(await Safe(() => chat.HealthAsync(ct), "chat"));

        var probe = await ProbeEmbedderAsync(ct);

        var status = providers.Any(p => p.Status is ProviderHealth.Unreachable) ? "unhealthy"
            : providers.Any(p => p.Status is ProviderHealth.Paused) ? "paused"
            : providers.Any(p => p.Status is ProviderHealth.Degraded or ProviderHealth.NotConfigured) ? "degraded"
            : "ok";

        return new HealthReport(status, providers, probe, caches.Stats(), new Dictionary<string, string>
        {
            ["providers.vector"] = vectors.Kind,
            ["providers.graph"] = graph.Kind,
            ["providers.chat"] = chat.Kind,
            ["chatModel"] = chat.ChatModel,
            ["extractionModel"] = chat.ExtractionModel,
            ["extraction.mode"] = _config.Extraction.DefaultMode,
            ["extraction.chunkCap"] = _config.Extraction.ChunkCap.ToString(),
            ["extraction.sourceStrategy"] = _config.Extraction.SourceStrategy,
            ["retrieval.refusalText"] = _config.Retrieval.RefusalText,
            ["replay"] = _config.Replay.Enabled ? "on" : "off",
            ["recording"] = _config.Replay.Record ? "on" : "off",
        });
    }

    private static async Task<ProviderHealth> Safe(Func<Task<ProviderHealth>> probe, string name)
    {
        try { return await probe(); }
        catch (Exception ex) { return new ProviderHealth(name, ProviderHealth.Unreachable, ex.Message); }
    }

    private async Task<EmbedderProbe> ProbeEmbedderAsync(CancellationToken ct)
    {
        try
        {
            var vectors = await embedder.EmbedAsync(
            [
                "The cinematographer shot the film in Sri Lanka.",
                "The director of photography filmed the picture in Sri Lanka.",
                "Quarterly depreciation schedules for rolling stock assets."
            ], ct);

            var similar = VectorMath.Cosine(vectors[0], vectors[1]);
            var unrelated = VectorMath.Cosine(vectors[0], vectors[2]);
            var passed = similar > 0.7 && unrelated < 0.3;

            return new EmbedderProbe(true, Math.Round(similar, 4), Math.Round(unrelated, 4), passed,
                passed
                    ? "Similar pair above 0.7 and unrelated pair below 0.3, as the phase 1 acceptance test requires."
                    : embedder.IsRealModel
                        ? "Outside the expected band. Check that the ONNX export is the sentence-transformers all-MiniLM-L6-v2 model."
                        : "The dev stand-in is a bag of words; it will not meet the acceptance band and is not meant to.");
        }
        catch (Exception ex)
        {
            return new EmbedderProbe(false, 0, 0, false, ex.Message);
        }
    }
}
