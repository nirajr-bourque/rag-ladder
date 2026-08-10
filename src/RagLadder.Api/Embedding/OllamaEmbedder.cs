using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using RagLadder.Api.Configuration;

namespace RagLadder.Api.Embedding;

/// <summary>
/// Embeddings served by Ollama rather than by a local ONNX file.
///
/// This exists for networks where huggingface.co is blocked but the Ollama endpoint is not — a
/// common corporate posture, and otherwise a dead end, because every route to the MiniLM weights
/// runs through Hugging Face. It trades in-process speed for a network hop and gives up the
/// "costs nothing per call" property, so prefer the local ONNX model when you can get it.
///
/// The vector width is whatever the chosen model returns; nothing in the pipeline assumes 384.
/// </summary>
public sealed class OllamaEmbedder : IEmbedder
{
    private readonly HttpClient _http;
    private readonly EmbeddingOptions _options;
    private readonly ILogger<OllamaEmbedder> _log;
    private readonly SemaphoreSlim _gate;
    private int _dimensions;
    private bool _legacyEndpoint;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ModelId => _options.OllamaModel;
    public int Dimensions => _dimensions > 0 ? _dimensions : _options.Dimensions;
    public bool IsRealModel => true;

    public OllamaEmbedder(HttpClient http, IOptions<RagLadderOptions> options, ILogger<OllamaEmbedder> log)
    {
        var cfg = options.Value;
        _options = cfg.Embedding;
        _log = log;
        _gate = new SemaphoreSlim(Math.Max(1, cfg.Ollama.MaxConcurrency));
        _http = http;
        _http.BaseAddress = new Uri(cfg.Ollama.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(cfg.Ollama.TimeoutSeconds);
        if (!string.IsNullOrWhiteSpace(cfg.Ollama.ApiKey))
            _http.DefaultRequestHeaders.Authorization = new("Bearer", cfg.Ollama.ApiKey);
    }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var results = new List<float[]>(texts.Count);

        foreach (var batch in texts.Chunk(Math.Max(1, _options.BatchSize)))
        {
            ct.ThrowIfCancellationRequested();
            await _gate.WaitAsync(ct);
            try
            {
                results.AddRange(await EmbedBatchAsync(batch, ct));
            }
            finally { _gate.Release(); }
        }

        return results;
    }

    private async Task<IReadOnlyList<float[]>> EmbedBatchAsync(string[] batch, CancellationToken ct)
    {
        if (!_legacyEndpoint)
        {
            var response = await _http.PostAsJsonAsync("api/embed",
                new { model = _options.OllamaModel, input = batch }, Json, ct);

            if (response.IsSuccessStatusCode)
            {
                var payload = await response.Content.ReadFromJsonAsync<EmbedResponse>(Json, ct);
                if (payload?.Embeddings is { Count: > 0 }) return Finish(payload.Embeddings);
            }
            else if (response.StatusCode is System.Net.HttpStatusCode.NotFound)
            {
                // Older Ollama builds only expose /api/embeddings, one text at a time.
                _log.LogInformation("Ollama /api/embed not available; falling back to /api/embeddings.");
                _legacyEndpoint = true;
            }
            else
            {
                var detail = await response.Content.ReadAsStringAsync(ct);
                throw new EmbeddingException($"Ollama embedding call failed ({(int)response.StatusCode}): {Trim(detail)}");
            }
        }

        var vectors = new List<List<float>>(batch.Length);
        foreach (var text in batch)
        {
            var response = await _http.PostAsJsonAsync("api/embeddings",
                new { model = _options.OllamaModel, prompt = text }, Json, ct);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(ct);
                throw new EmbeddingException($"Ollama embedding call failed ({(int)response.StatusCode}): {Trim(detail)}");
            }
            var payload = await response.Content.ReadFromJsonAsync<LegacyEmbedResponse>(Json, ct);
            if (payload?.Embedding is null or { Count: 0 })
                throw new EmbeddingException($"Ollama returned no embedding for model '{_options.OllamaModel}'.");
            vectors.Add(payload.Embedding);
        }
        return Finish(vectors);
    }

    private float[][] Finish(List<List<float>> raw)
    {
        var result = new float[raw.Count][];
        for (var i = 0; i < raw.Count; i++)
        {
            var vector = raw[i].ToArray();
            // Cosine similarity is what the stores use, so normalise here as the ONNX path does.
            VectorMath.L2Normalize(vector);
            result[i] = vector;
        }

        if (_dimensions == 0 && result.Length > 0)
        {
            _dimensions = result[0].Length;
            if (_dimensions != _options.Dimensions)
                _log.LogInformation(
                    "Embedding model '{Model}' returns {Actual} dimensions (configuration said {Configured}). " +
                    "Collections are created from the actual width, so this is fine — but a document indexed " +
                    "with a different model must be reprocessed.",
                    _options.OllamaModel, _dimensions, _options.Dimensions);
        }
        return result;
    }

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "…";
}

public sealed class EmbeddingException(string message) : Exception(message);

file sealed class EmbedResponse
{
    public List<List<float>>? Embeddings { get; init; }
}

file sealed class LegacyEmbedResponse
{
    [JsonPropertyName("embedding")] public List<float>? Embedding { get; init; }
}
