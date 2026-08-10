using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RagLadder.Api.Configuration;
using RagLadder.Api.Embedding;
using OnnxSessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions;

namespace RagLadder.Api.Reranking;

public interface IReranker
{
    string ModelId { get; }
    bool IsRealModel { get; }
    Task<IReadOnlyList<double>> ScoreAsync(string query, IReadOnlyList<string> passages, CancellationToken ct = default);
}

/// <summary>
/// ms-marco-MiniLM-L-6-v2 cross-encoder through ONNX Runtime. Scores each (query, passage) pair
/// jointly, which is why it moves a credit buried 400 tokens into a crew list from rank 12 to
/// rank 1 when cosine similarity cannot (spec §7.5 stage 5).
/// </summary>
public sealed class OnnxReranker : IReranker, IDisposable
{
    private readonly InferenceSession _session;
    private readonly WordPieceTokenizer _tokenizer;
    private readonly RerankOptions _options;
    private readonly string[] _inputNames;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string ModelId => _options.ModelId;
    public bool IsRealModel => true;

    public OnnxReranker(RerankOptions options)
    {
        _options = options;
        if (!File.Exists(options.ModelPath))
            throw new FileNotFoundException($"Reranker model not found at '{options.ModelPath}'. Run tools/fetch-models.ps1.", options.ModelPath);
        if (!File.Exists(options.VocabPath))
            throw new FileNotFoundException($"Reranker vocab not found at '{options.VocabPath}'. Run tools/fetch-models.ps1.", options.VocabPath);

        _session = new InferenceSession(options.ModelPath,
            new OnnxSessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL });
        _tokenizer = WordPieceTokenizer.FromFile(options.VocabPath);
        _inputNames = [.. _session.InputMetadata.Keys];
    }

    public async Task<IReadOnlyList<double>> ScoreAsync(string query, IReadOnlyList<string> passages, CancellationToken ct = default)
    {
        if (passages.Count == 0) return [];
        var scores = new double[passages.Count];
        const int batchSize = 8;

        for (var offset = 0; offset < passages.Count; offset += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = passages.Skip(offset).Take(batchSize).ToArray();
            await _gate.WaitAsync(ct);
            try
            {
                var batchScores = RunBatch(query, batch);
                for (var i = 0; i < batchScores.Length; i++) scores[offset + i] = batchScores[i];
            }
            finally { _gate.Release(); }
        }
        return scores;
    }

    private double[] RunBatch(string query, string[] passages)
    {
        var encoded = passages.Select(p => _tokenizer.EncodePair(query, p, _options.MaxTokens)).ToArray();
        var maxLen = encoded.Max(e => e.Length);
        var batch = passages.Length;

        var ids = new DenseTensor<long>([batch, maxLen]);
        var mask = new DenseTensor<long>([batch, maxLen]);
        var types = new DenseTensor<long>([batch, maxLen]);

        for (var b = 0; b < batch; b++)
        for (var t = 0; t < maxLen; t++)
        {
            var inRange = t < encoded[b].Length;
            ids[b, t] = inRange ? encoded[b].Ids[t] : _tokenizer.PadId;
            mask[b, t] = inRange ? 1 : 0;
            types[b, t] = inRange ? encoded[b].TypeIds[t] : 0;
        }

        var inputs = new List<NamedOnnxValue>();
        foreach (var name in _inputNames)
        {
            inputs.Add(name switch
            {
                "input_ids" => NamedOnnxValue.CreateFromTensor(name, ids),
                "attention_mask" => NamedOnnxValue.CreateFromTensor(name, mask),
                "token_type_ids" => NamedOnnxValue.CreateFromTensor(name, types),
                _ => NamedOnnxValue.CreateFromTensor(name, ids)
            });
        }

        using var outputs = _session.Run(inputs);
        var logits = outputs.First().AsTensor<float>();
        var scores = new double[batch];
        var width = logits.Dimensions.Length > 1 ? logits.Dimensions[1] : 1;
        for (var b = 0; b < batch; b++)
        {
            // Single-logit relevance head; two-logit heads use the positive class.
            scores[b] = width == 1 ? logits[b, 0] : logits[b, width - 1];
        }
        return scores;
    }

    public void Dispose()
    {
        _session.Dispose();
        _gate.Dispose();
    }
}

/// <summary>
/// Development stand-in for the cross-encoder: BM25-flavoured lexical overlap with a proximity
/// bonus. Reports itself as not-real so health can flag the degradation.
/// </summary>
public sealed class LexicalReranker : IReranker
{
    public string ModelId => "lexical-dev-reranker";
    public bool IsRealModel => false;

    public Task<IReadOnlyList<double>> ScoreAsync(string query, IReadOnlyList<string> passages, CancellationToken ct = default)
    {
        var queryTerms = WordPieceTokenizer.BasicTokenize(query).Where(t => t.Length > 2).Distinct().ToArray();
        var scores = new List<double>(passages.Count);

        foreach (var passage in passages)
        {
            var tokens = WordPieceTokenizer.BasicTokenize(passage);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var t in tokens) counts[t] = counts.GetValueOrDefault(t) + 1;

            double score = 0;
            var matched = 0;
            foreach (var term in queryTerms)
            {
                if (!counts.TryGetValue(term, out var tf)) continue;
                matched++;
                // Saturating term frequency, damped by passage length.
                score += tf * 2.2 / (tf + 1.2 * (0.25 + 0.75 * tokens.Count / 180.0));
            }
            if (queryTerms.Length > 0) score *= 0.5 + 0.5 * matched / queryTerms.Length;
            scores.Add(score);
        }
        return Task.FromResult<IReadOnlyList<double>>(scores);
    }
}
