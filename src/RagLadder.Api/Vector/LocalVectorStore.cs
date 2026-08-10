using System.Text.Json;
using RagLadder.Api.Embedding;
using RagLadder.Api.Infrastructure;
using RagLadder.Api.Llm;
using RagLadder.Api.Models;

namespace RagLadder.Api.Vector;

/// <summary>
/// SQLite-backed vector store used when Qdrant Cloud is not configured. Brute-force cosine over
/// a few hundred chunks costs microseconds, and keeping the vectors in the same file as the rest
/// of the state means the demo survives a restart with no hosted dependency at all.
/// </summary>
public sealed class LocalVectorStore(Db db) : IVectorStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Kind => "local-sqlite";

    public Task EnsureCollectionAsync(string collection, int dimensions, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task DeleteCollectionAsync(string collection, CancellationToken ct = default)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM vectors WHERE collection = $c";
        cmd.Parameters.AddWithValue("$c", collection);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task UpsertAsync(string collection, IReadOnlyList<VectorPoint> points, CancellationToken ct = default)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var p in points)
        {
            ct.ThrowIfCancellationRequested();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO vectors (collection, chunk_id, vector, payload) VALUES ($c, $id, $v, $p)
                ON CONFLICT(collection, chunk_id) DO UPDATE SET vector = excluded.vector, payload = excluded.payload
                """;
            cmd.Parameters.AddWithValue("$c", collection);
            cmd.Parameters.AddWithValue("$id", p.ChunkId);
            cmd.Parameters.AddWithValue("$v", CacheRepository.ToBlob(p.Vector));
            cmd.Parameters.AddWithValue("$p", JsonSerializer.Serialize(p.Payload, Json));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VectorHit>> SearchAsync(string collection, float[] query, int limit, ChunkFilter? filter, CancellationToken ct = default)
    {
        var rows = Load(collection, filter);
        var hits = rows
            .Select(r => new VectorHit(r.ChunkId, VectorMath.Cosine(query, r.Vector), r.Payload))
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.ChunkId, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
        return Task.FromResult<IReadOnlyList<VectorHit>>(hits);
    }

    public Task<IReadOnlyList<VectorHit>> KeywordSearchAsync(string collection, string queryText, int limit, ChunkFilter? filter, CancellationToken ct = default)
    {
        var rows = Load(collection, filter);
        var byId = rows.ToDictionary(r => r.ChunkId, r => r.Payload, StringComparer.Ordinal);
        var scored = Bm25.Score([.. rows.Select(r => (r.ChunkId, r.Payload.Text))], queryText, limit);
        var hits = scored.Select(s => new VectorHit(s.Id, s.Score, byId[s.Id])).ToList();
        return Task.FromResult<IReadOnlyList<VectorHit>>(hits);
    }

    public Task<int> CountAsync(string collection, CancellationToken ct = default)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM vectors WHERE collection = $c";
        cmd.Parameters.AddWithValue("$c", collection);
        return Task.FromResult(Convert.ToInt32(cmd.ExecuteScalar()));
    }

    public Task<ProviderHealth> HealthAsync(CancellationToken ct = default)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(DISTINCT collection), COUNT(*) FROM vectors";
        using var r = cmd.ExecuteReader();
        var (collections, points) = r.Read() ? (r.GetInt32(0), r.GetInt32(1)) : (0, 0);
        return Task.FromResult(new ProviderHealth("vector", ProviderHealth.Ok,
            $"Local SQLite store: {collections} collections, {points} points. Qdrant Cloud not configured."));
    }

    private List<(string ChunkId, float[] Vector, ChunkPayload Payload)> Load(string collection, ChunkFilter? filter)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT chunk_id, vector, payload FROM vectors WHERE collection = $c";
        cmd.Parameters.AddWithValue("$c", collection);
        using var reader = cmd.ExecuteReader();

        var rows = new List<(string, float[], ChunkPayload)>();
        while (reader.Read())
        {
            var payload = JsonSerializer.Deserialize<ChunkPayload>(reader.GetString(2), Json);
            if (payload is null) continue;
            if (filter is not null && !filter.IsEmpty && !payload.Matches(filter)) continue;
            rows.Add((reader.GetString(0), CacheRepository.FromBlob((byte[])reader["vector"]), payload));
        }
        return rows;
    }
}
