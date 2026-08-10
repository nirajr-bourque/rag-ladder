using RagLadder.Api.Infrastructure;

namespace RagLadder.Api.Embedding;

/// <summary>
/// Wraps an embedder with the content-hash cache. A warm-cache reprocess of an unchanged
/// document must make zero embedder calls (spec §11 phase 3 acceptance).
/// </summary>
public sealed class CachingEmbedder(IEmbedder inner, CacheRepository cache) : IEmbedder
{
    public string ModelId => inner.ModelId;
    public int Dimensions => inner.Dimensions;
    public bool IsRealModel => inner.IsRealModel;

    /// <summary>Counts model calls actually made — surfaced by the processing job for the acceptance test.</summary>
    public int ComputedCount { get; private set; }
    public int CacheHitCount { get; private set; }

    public void ResetCounters()
    {
        ComputedCount = 0;
        CacheHitCount = 0;
    }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var hashes = texts.Select(t => CacheRepository.EmbeddingKey(t, ModelId)).ToArray();
        var cached = cache.GetEmbeddings(hashes.Distinct().ToArray());

        var missingIndexes = new List<int>();
        for (var i = 0; i < texts.Count; i++)
            if (!cached.ContainsKey(hashes[i]))
                missingIndexes.Add(i);

        CacheHitCount += texts.Count - missingIndexes.Count;

        if (missingIndexes.Count > 0)
        {
            // Deduplicate identical texts inside the same batch before calling the model.
            var uniqueByHash = new Dictionary<string, string>();
            foreach (var i in missingIndexes) uniqueByHash.TryAdd(hashes[i], texts[i]);

            var order = uniqueByHash.Keys.ToArray();
            var computed = await inner.EmbedAsync([.. order.Select(h => uniqueByHash[h])], ct);
            ComputedCount += order.Length;

            var fresh = new Dictionary<string, float[]>(order.Length);
            for (var i = 0; i < order.Length; i++)
            {
                fresh[order[i]] = computed[i];
                cached[order[i]] = computed[i];
            }
            cache.PutEmbeddings(ModelId, fresh);
        }

        return [.. hashes.Select(h => cached[h])];
    }
}
