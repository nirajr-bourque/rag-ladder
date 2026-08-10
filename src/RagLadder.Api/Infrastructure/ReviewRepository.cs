using System.Text.Json;
using RagLadder.Api.Models;

namespace RagLadder.Api.Infrastructure;

/// <summary>Review-gate state, golden sets and eval runs.</summary>
public sealed class ReviewRepository(Db db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void SaveExtraction(ExtractionResult result)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO extraction_results (doc_id, payload) VALUES ($id, $p)
            ON CONFLICT(doc_id) DO UPDATE SET payload = excluded.payload
            """;
        cmd.Parameters.AddWithValue("$id", result.DocId);
        cmd.Parameters.AddWithValue("$p", JsonSerializer.Serialize(result, Json));
        cmd.ExecuteNonQuery();
    }

    public ExtractionResult? GetExtraction(string docId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload FROM extraction_results WHERE doc_id = $id";
        cmd.Parameters.AddWithValue("$id", docId);
        return cmd.ExecuteScalar() is string s
            ? JsonSerializer.Deserialize<ExtractionResult>(s, Json)
            : null;
    }

    // ----- rejections (persist by triple hash, spec §6.7) ------------------

    public HashSet<string> GetRejections(string docId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT triple_hash FROM triple_rejections WHERE doc_id = $id";
        cmd.Parameters.AddWithValue("$id", docId);
        using var r = cmd.ExecuteReader();
        var set = new HashSet<string>(StringComparer.Ordinal);
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }

    public void AddRejections(string docId, IEnumerable<string> hashes)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var h in hashes)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO triple_rejections (doc_id, triple_hash) VALUES ($d, $h) ON CONFLICT DO NOTHING";
            cmd.Parameters.AddWithValue("$d", docId);
            cmd.Parameters.AddWithValue("$h", h);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void RemoveRejections(string docId, IEnumerable<string> hashes)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var h in hashes)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM triple_rejections WHERE doc_id = $d AND triple_hash = $h";
            cmd.Parameters.AddWithValue("$d", docId);
            cmd.Parameters.AddWithValue("$h", h);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // ----- merge decisions -------------------------------------------------

    public Dictionary<(string, string), string> GetMergeDecisions(string docId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT left_key, right_key, decision FROM merge_decisions WHERE doc_id = $id";
        cmd.Parameters.AddWithValue("$id", docId);
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<(string, string), string>();
        while (r.Read()) map[(r.GetString(0), r.GetString(1))] = r.GetString(2);
        return map;
    }

    public void SaveMergeDecision(string docId, string left, string right, string decision)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO merge_decisions (doc_id, left_key, right_key, decision) VALUES ($d, $l, $r, $dec)
            ON CONFLICT(doc_id, left_key, right_key) DO UPDATE SET decision = excluded.decision
            """;
        cmd.Parameters.AddWithValue("$d", docId);
        cmd.Parameters.AddWithValue("$l", left);
        cmd.Parameters.AddWithValue("$r", right);
        cmd.Parameters.AddWithValue("$dec", decision);
        cmd.ExecuteNonQuery();
    }

    // ----- golden sets -----------------------------------------------------

    public void SaveGolden(string docId, GoldenSet set)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO golden_sets (doc_id, payload) VALUES ($id, $p)
            ON CONFLICT(doc_id) DO UPDATE SET payload = excluded.payload
            """;
        cmd.Parameters.AddWithValue("$id", docId);
        cmd.Parameters.AddWithValue("$p", JsonSerializer.Serialize(set, Json));
        cmd.ExecuteNonQuery();
    }

    public GoldenSet? GetGolden(string docId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload FROM golden_sets WHERE doc_id = $id";
        cmd.Parameters.AddWithValue("$id", docId);
        return cmd.ExecuteScalar() is string s ? JsonSerializer.Deserialize<GoldenSet>(s, Json) : null;
    }

    // ----- eval runs -------------------------------------------------------

    public void SaveEvalRun(EvalRun run)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO eval_runs (run_id, doc_id, started, payload) VALUES ($r, $d, $s, $p)
            ON CONFLICT(run_id) DO UPDATE SET payload = excluded.payload
            """;
        cmd.Parameters.AddWithValue("$r", run.RunId);
        cmd.Parameters.AddWithValue("$d", run.DocumentId);
        cmd.Parameters.AddWithValue("$s", run.StartedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$p", JsonSerializer.Serialize(run, Json));
        cmd.ExecuteNonQuery();
    }

    public EvalRun? GetEvalRun(string runId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload FROM eval_runs WHERE run_id = $r";
        cmd.Parameters.AddWithValue("$r", runId);
        return cmd.ExecuteScalar() is string s ? JsonSerializer.Deserialize<EvalRun>(s, Json) : null;
    }

    public IReadOnlyList<EvalRun> ListEvalRuns(string docId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload FROM eval_runs WHERE doc_id = $d ORDER BY started DESC LIMIT 20";
        cmd.Parameters.AddWithValue("$d", docId);
        using var r = cmd.ExecuteReader();
        var list = new List<EvalRun>();
        while (r.Read())
        {
            var run = JsonSerializer.Deserialize<EvalRun>(r.GetString(0), Json);
            if (run is not null) list.Add(run);
        }
        return list;
    }
}
