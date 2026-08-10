using System.Text;

namespace RagLadder.Api.Embedding;

/// <summary>
/// Deterministic development stand-in used when the ONNX model files are absent. Hashes word
/// unigrams and bigrams into the embedding space with sublinear term frequency. It is a bag of
/// words, not a language model: it will rank lexical overlap sensibly and nothing else, which is
/// enough to exercise the pipeline end to end. Health always reports this as degraded so it can
/// never be mistaken for the real embedder during a demo.
/// </summary>
public sealed class HashEmbedder(int dimensions = 384) : IEmbedder
{
    public string ModelId => "hash-dev-embedder";
    public int Dimensions { get; } = dimensions;
    public bool IsRealModel => false;

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var result = new List<float[]>(texts.Count);
        foreach (var text in texts)
        {
            ct.ThrowIfCancellationRequested();
            result.Add(Embed(text));
        }
        return Task.FromResult<IReadOnlyList<float[]>>(result);
    }

    private float[] Embed(string text)
    {
        var vec = new float[Dimensions];
        var tokens = WordPieceTokenizer.BasicTokenize(text).Where(t => t.Length > 1).ToArray();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var token in tokens)
            counts[token] = counts.GetValueOrDefault(token) + 1;
        for (var i = 0; i + 1 < tokens.Length; i++)
        {
            var bigram = tokens[i] + "_" + tokens[i + 1];
            counts[bigram] = counts.GetValueOrDefault(bigram) + 1;
        }

        foreach (var (term, count) in counts)
        {
            var h = Fnv(term);
            var slot = (int)(h % (uint)Dimensions);
            var sign = (h & 0x8000_0000u) != 0 ? -1f : 1f;
            vec[slot] += sign * (float)(1 + Math.Log(count));
        }

        VectorMath.L2Normalize(vec);
        return vec;
    }

    private static uint Fnv(string s)
    {
        const uint prime = 16777619;
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            hash ^= b;
            hash *= prime;
        }
        return hash;
    }
}
