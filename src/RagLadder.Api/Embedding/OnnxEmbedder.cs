using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RagLadder.Api.Configuration;
using OnnxSessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions;

namespace RagLadder.Api.Embedding;

/// <summary>
/// all-MiniLM-L6-v2 through ONNX Runtime, in-process. Mean-pools the token embeddings with the
/// attention mask and L2-normalises, matching the sentence-transformers reference implementation.
/// </summary>
public sealed class OnnxEmbedder : IEmbedder, IDisposable
{
    private readonly InferenceSession _session;
    private readonly WordPieceTokenizer _tokenizer;
    private readonly EmbeddingOptions _options;
    private readonly string[] _inputNames;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string ModelId => _options.ModelId;
    public int Dimensions => _options.Dimensions;
    public bool IsRealModel => true;

    public OnnxEmbedder(EmbeddingOptions options)
    {
        _options = options;
        if (!File.Exists(options.ModelPath))
            throw new FileNotFoundException($"Embedding model not found at '{options.ModelPath}'. Run tools/fetch-models.ps1.", options.ModelPath);
        if (!File.Exists(options.VocabPath))
            throw new FileNotFoundException($"Embedding vocab not found at '{options.VocabPath}'. Run tools/fetch-models.ps1.", options.VocabPath);

        var sessionOptions = new OnnxSessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _session = new InferenceSession(options.ModelPath, sessionOptions);
        _tokenizer = WordPieceTokenizer.FromFile(options.VocabPath);
        _inputNames = [.. _session.InputMetadata.Keys];
    }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var results = new float[texts.Count][];
        for (var offset = 0; offset < texts.Count; offset += _options.BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = texts.Skip(offset).Take(_options.BatchSize).ToArray();
            await _gate.WaitAsync(ct);
            try
            {
                var vectors = RunBatch(batch);
                for (var i = 0; i < vectors.Length; i++) results[offset + i] = vectors[i];
            }
            finally { _gate.Release(); }
        }
        return results;
    }

    private float[][] RunBatch(string[] texts)
    {
        // A 400-token chunk does not fit a 256-token model, so each text becomes one or more
        // overlapping windows and the resulting vectors are averaged back together. Without this
        // the tail of every chunk is invisible to retrieval.
        var windowsPerText = texts.Select(t => _tokenizer.EncodeWindows(t, _options.MaxTokens)).ToArray();
        var encoded = windowsPerText.SelectMany(w => w).ToArray();
        var maxLen = encoded.Max(e => e.Length);
        var batch = encoded.Length;

        var ids = new DenseTensor<long>([batch, maxLen]);
        var mask = new DenseTensor<long>([batch, maxLen]);
        var types = new DenseTensor<long>([batch, maxLen]);

        for (var b = 0; b < batch; b++)
        {
            for (var t = 0; t < maxLen; t++)
            {
                var inRange = t < encoded[b].Length;
                ids[b, t] = inRange ? encoded[b].Ids[t] : _tokenizer.PadId;
                mask[b, t] = inRange ? 1 : 0;
                types[b, t] = 0;
            }
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
        var sentence = outputs.FirstOrDefault(o => o.Name is "sentence_embedding");
        var windowVectors = sentence is not null
            ? Split(sentence.AsTensor<float>(), batch, _options.Dimensions)
            : MeanPool((outputs.FirstOrDefault(o => o.Name is "last_hidden_state") ?? outputs.First()).AsTensor<float>(),
                mask, batch, maxLen);

        return CombineWindows(windowVectors, windowsPerText);
    }

    /// <summary>Averages a text's window vectors back into one, then re-normalises.</summary>
    private static float[][] CombineWindows(float[][] windowVectors, IReadOnlyList<Encoded>[] windowsPerText)
    {
        var result = new float[windowsPerText.Length][];
        var cursor = 0;

        for (var t = 0; t < windowsPerText.Length; t++)
        {
            var count = windowsPerText[t].Count;
            if (count == 1) { result[t] = windowVectors[cursor++]; continue; }

            var combined = new float[windowVectors[cursor].Length];
            for (var w = 0; w < count; w++)
            {
                var vector = windowVectors[cursor + w];
                for (var d = 0; d < combined.Length; d++) combined[d] += vector[d];
            }
            for (var d = 0; d < combined.Length; d++) combined[d] /= count;
            VectorMath.L2Normalize(combined);

            result[t] = combined;
            cursor += count;
        }
        return result;
    }

    private static float[][] Split(Tensor<float> tensor, int batch, int dim)
    {
        var result = new float[batch][];
        for (var b = 0; b < batch; b++)
        {
            var vec = new float[dim];
            for (var d = 0; d < dim; d++) vec[d] = tensor[b, d];
            VectorMath.L2Normalize(vec);
            result[b] = vec;
        }
        return result;
    }

    private static float[][] MeanPool(Tensor<float> hidden, DenseTensor<long> mask, int batch, int seq)
    {
        var dim = hidden.Dimensions[^1];
        var result = new float[batch][];
        for (var b = 0; b < batch; b++)
        {
            var vec = new float[dim];
            long count = 0;
            for (var t = 0; t < seq; t++)
            {
                if (mask[b, t] == 0) continue;
                count++;
                for (var d = 0; d < dim; d++) vec[d] += hidden[b, t, d];
            }
            if (count > 0)
                for (var d = 0; d < dim; d++) vec[d] /= count;
            VectorMath.L2Normalize(vec);
            result[b] = vec;
        }
        return result;
    }

    public void Dispose()
    {
        _session.Dispose();
        _gate.Dispose();
    }
}
