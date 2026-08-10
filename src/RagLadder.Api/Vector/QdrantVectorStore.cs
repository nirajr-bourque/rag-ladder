using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using RagLadder.Api.Configuration;
using RagLadder.Api.Llm;
using RagLadder.Api.Models;

namespace RagLadder.Api.Vector;

/// <summary>
/// Qdrant Cloud over the REST API.
///
/// Two things are worth knowing. First, the payload's <c>text</c> field gets a full-text index at
/// collection creation — without it the stage-4 keyword arm cannot run at all (spec §5.3).
/// Second, the keyword arm retrieves via a full-text <c>should</c> clause and then scores locally
/// with BM25: Qdrant's text filter is a matcher, not a ranker, and the demo needs per-arm scores
/// to show that "$47.3M" was found only by the keyword side.
/// </summary>
public sealed class QdrantVectorStore : IVectorStore
{
    private readonly HttpClient _http;
    private readonly ILogger<QdrantVectorStore> _log;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string Kind => "qdrant";

    public QdrantVectorStore(HttpClient http, IOptions<RagLadderOptions> options, ILogger<QdrantVectorStore> log)
    {
        var cfg = options.Value.Qdrant;
        _log = log;
        _http = http;
        _http.BaseAddress = new Uri(cfg.Url.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(cfg.TimeoutSeconds);
        if (!string.IsNullOrWhiteSpace(cfg.ApiKey)) _http.DefaultRequestHeaders.Add("api-key", cfg.ApiKey);
    }

    public async Task EnsureCollectionAsync(string collection, int dimensions, CancellationToken ct = default)
    {
        using var existing = await _http.GetAsync($"collections/{collection}", ct);
        if (existing.IsSuccessStatusCode) return;

        var create = new JsonObject
        {
            ["vectors"] = new JsonObject { ["size"] = dimensions, ["distance"] = "Cosine" }
        };
        using var response = await _http.PutAsJsonAsync($"collections/{collection}", create, Json, ct);
        await EnsureSuccess(response, $"create collection {collection}", ct);

        // Keyword / integer indexes for the metadata filters, and the mandatory full-text index.
        foreach (var (field, schema) in new (string, JsonNode)[]
                 {
                     ("chunkId", "keyword"), ("docId", "keyword"), ("section", "keyword"),
                     ("docType", "keyword"), ("subject", "keyword"), ("studio", "keyword"),
                     ("market", "keyword"), ("entityKeys", "keyword"),
                     ("year", "integer"), ("page", "integer"),
                     ("text", new JsonObject
                     {
                         ["type"] = "text",
                         ["tokenizer"] = "word",
                         ["lowercase"] = true,
                         ["min_token_len"] = 2,
                         ["max_token_len"] = 30
                     })
                 })
        {
            var body = new JsonObject { ["field_name"] = field, ["field_schema"] = schema };
            using var idx = await _http.PutAsJsonAsync($"collections/{collection}/index?wait=true", body, Json, ct);
            if (!idx.IsSuccessStatusCode)
                _log.LogWarning("Qdrant index on '{Field}' for {Collection} returned {Status}.", field, collection, (int)idx.StatusCode);
        }
    }

    public async Task DeleteCollectionAsync(string collection, CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync($"collections/{collection}", ct);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
            _log.LogWarning("Deleting collection {Collection} returned {Status}.", collection, (int)response.StatusCode);
    }

    public async Task UpsertAsync(string collection, IReadOnlyList<VectorPoint> points, CancellationToken ct = default)
    {
        foreach (var batch in points.Chunk(256))
        {
            var body = new JsonObject
            {
                ["points"] = new JsonArray([.. batch.Select(p => (JsonNode)new JsonObject
                {
                    ["id"] = PointId(p.ChunkId),
                    ["vector"] = new JsonArray([.. p.Vector.Select(v => (JsonNode)JsonValue.Create(v))]),
                    ["payload"] = JsonSerializer.SerializeToNode(p.Payload, Json)
                })])
            };
            using var response = await _http.PutAsJsonAsync($"collections/{collection}/points?wait=true", body, Json, ct);
            await EnsureSuccess(response, $"upsert into {collection}", ct);
        }
    }

    public async Task<IReadOnlyList<VectorHit>> SearchAsync(string collection, float[] query, int limit, ChunkFilter? filter, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["vector"] = new JsonArray([.. query.Select(v => (JsonNode)JsonValue.Create(v))]),
            ["limit"] = limit,
            ["with_payload"] = true,
        };
        if (BuildFilter(filter) is { } f) body["filter"] = f;

        using var response = await _http.PostAsJsonAsync($"collections/{collection}/points/search", body, Json, ct);
        await EnsureSuccess(response, $"search {collection}", ct);

        var payload = await response.Content.ReadFromJsonAsync<SearchResponse>(Json, ct);
        return [.. (payload?.Result ?? []).Select(r => new VectorHit(
            r.Payload?.ChunkId ?? "", r.Score, r.Payload ?? Empty()))];
    }

    public async Task<IReadOnlyList<VectorHit>> KeywordSearchAsync(string collection, string queryText, int limit, ChunkFilter? filter, CancellationToken ct = default)
    {
        var tokens = Bm25.QueryTokens(queryText);
        if (tokens.Count == 0) return [];

        // OR over the query tokens: Qdrant's text match requires every word in the phrase to be
        // present, which is far too strict for a whole question.
        var should = new JsonArray([.. tokens.Select(t => (JsonNode)new JsonObject
        {
            ["key"] = "text",
            ["match"] = new JsonObject { ["text"] = t }
        })]);

        var combined = new JsonObject { ["should"] = should };
        if (BuildFilter(filter) is JsonObject { } mustFilter && mustFilter["must"] is JsonArray must)
            combined["must"] = must.DeepClone();

        var body = new JsonObject
        {
            ["filter"] = combined,
            ["limit"] = Math.Max(limit * 4, 64),
            ["with_payload"] = true,
            ["with_vector"] = false,
        };

        using var response = await _http.PostAsJsonAsync($"collections/{collection}/points/scroll", body, Json, ct);
        await EnsureSuccess(response, $"keyword scroll {collection}", ct);

        var payload = await response.Content.ReadFromJsonAsync<ScrollResponse>(Json, ct);
        var candidates = (payload?.Result?.Points ?? [])
            .Where(p => p.Payload is not null)
            .Select(p => p.Payload!)
            .ToList();

        var byId = candidates.ToDictionary(p => p.ChunkId, p => p, StringComparer.Ordinal);
        var scored = Bm25.Score([.. candidates.Select(p => (p.ChunkId, p.Text))], queryText, limit);
        return [.. scored.Select(s => new VectorHit(s.Id, s.Score, byId[s.Id]))];
    }

    public async Task<int> CountAsync(string collection, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync($"collections/{collection}/points/count",
            new JsonObject { ["exact"] = true }, Json, ct);
        if (!response.IsSuccessStatusCode) return 0;
        var node = await response.Content.ReadFromJsonAsync<JsonNode>(Json, ct);
        return node?["result"]?["count"]?.GetValue<int>() ?? 0;
    }

    public async Task<ProviderHealth> HealthAsync(CancellationToken ct = default)
    {
        if (_http.BaseAddress is null || string.IsNullOrWhiteSpace(_http.BaseAddress.Host))
            return new ProviderHealth("vector", ProviderHealth.NotConfigured, "No Qdrant URL configured.");
        try
        {
            using var response = await _http.GetAsync("collections", ct);
            if (response.IsSuccessStatusCode)
            {
                var node = await response.Content.ReadFromJsonAsync<JsonNode>(Json, ct);
                var count = node?["result"]?["collections"]?.AsArray().Count ?? 0;
                return new ProviderHealth("vector", ProviderHealth.Ok, $"Qdrant reachable, {count} collections.");
            }

            // Free-tier clusters pause when idle; distinguishing paused from unreachable is the
            // single most useful health signal on demo day (spec §4.2).
            return response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.Forbidden
                ? new ProviderHealth("vector", ProviderHealth.Paused,
                    $"Qdrant returned {(int)response.StatusCode}. Free-tier clusters suspend when idle — resume it in the Qdrant Cloud console.")
                : new ProviderHealth("vector", ProviderHealth.Unreachable, $"Qdrant returned {(int)response.StatusCode}.");
        }
        catch (TaskCanceledException)
        {
            return new ProviderHealth("vector", ProviderHealth.Paused,
                "Qdrant did not respond before the timeout. A suspended free-tier cluster behaves this way — check the console.");
        }
        catch (HttpRequestException ex)
        {
            return new ProviderHealth("vector", ProviderHealth.Unreachable, ex.Message);
        }
    }

    private static JsonObject? BuildFilter(ChunkFilter? filter)
    {
        if (filter is null || filter.IsEmpty) return null;
        var must = new JsonArray();

        void Keyword(string key, string? value)
        {
            if (value is null) return;
            must.Add(new JsonObject { ["key"] = key, ["match"] = new JsonObject { ["value"] = value } });
        }

        Keyword("docType", filter.DocType);
        Keyword("subject", filter.Subject);
        Keyword("studio", filter.Studio);
        Keyword("market", filter.Market);
        Keyword("section", filter.Section);

        if (filter.Year is { } y)
            must.Add(new JsonObject { ["key"] = "year", ["match"] = new JsonObject { ["value"] = y } });
        if (filter.YearRange is { Length: 2 } range)
            must.Add(new JsonObject
            {
                ["key"] = "year",
                ["range"] = new JsonObject { ["gte"] = range[0], ["lte"] = range[1] }
            });

        return must.Count == 0 ? null : new JsonObject { ["must"] = must };
    }

    /// <summary>Qdrant point ids must be an unsigned integer or a UUID; chunk ids are neither.</summary>
    internal static string PointId(string chunkId)
    {
        var hash = MD5.HashData(System.Text.Encoding.UTF8.GetBytes(chunkId));
        return new Guid(hash).ToString();
    }

    private static ChunkPayload Empty() => new()
    {
        ChunkId = "", DocId = "", Section = "", Text = ""
    };

    private static async Task EnsureSuccess(HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(ct);
        throw new QdrantException($"Qdrant failed to {what}: {(int)response.StatusCode} {detail}");
    }

    private sealed class SearchResponse
    {
        public List<ScoredPoint>? Result { get; init; }
    }

    private sealed class ScoredPoint
    {
        public double Score { get; init; }
        public ChunkPayload? Payload { get; init; }
    }

    private sealed class ScrollResponse
    {
        public ScrollResult? Result { get; init; }
    }

    private sealed class ScrollResult
    {
        public List<ScrollPoint>? Points { get; init; }
    }

    private sealed class ScrollPoint
    {
        public ChunkPayload? Payload { get; init; }
    }
}

public sealed class QdrantException(string message) : Exception(message);
