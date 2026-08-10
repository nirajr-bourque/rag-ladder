namespace RagLadder.Api.Embedding;

public interface IEmbedder
{
    string ModelId { get; }
    int Dimensions { get; }
    /// <summary>False when running on the deterministic dev stand-in rather than the real ONNX model.</summary>
    bool IsRealModel { get; }
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}

public static class VectorMath
{
    public static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length) return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        if (na == 0 || nb == 0) return 0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    public static void L2Normalize(float[] v)
    {
        double sum = 0;
        foreach (var x in v) sum += x * x;
        var norm = Math.Sqrt(sum);
        if (norm < 1e-12) return;
        for (var i = 0; i < v.Length; i++) v[i] = (float)(v[i] / norm);
    }
}
