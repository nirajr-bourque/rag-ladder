using System.Text.Json;
using Microsoft.Data.Sqlite;
using RagLadder.Api.Models;

namespace RagLadder.Api.Infrastructure;

/// <summary>Documents, sections and chunks. The single source of truth for chunk text and offsets.</summary>
public sealed class CorpusRepository(Db db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ----- documents ------------------------------------------------------

    public void UpsertDocument(DocumentRecord doc, string? rawText = null, string? pdfPath = null)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO documents (id, title, file_name, page_count, uploaded_utc, status, graph_committed, raw_text, pdf_path)
            VALUES ($id, $title, $file, $pages, $up, $status, $committed, $raw, $pdf)
            ON CONFLICT(id) DO UPDATE SET
                title = excluded.title, page_count = excluded.page_count, status = excluded.status,
                graph_committed = excluded.graph_committed,
                raw_text = COALESCE(excluded.raw_text, documents.raw_text),
                pdf_path = COALESCE(excluded.pdf_path, documents.pdf_path);
            """;
        cmd.Parameters.AddWithValue("$id", doc.Id);
        cmd.Parameters.AddWithValue("$title", doc.Title);
        cmd.Parameters.AddWithValue("$file", doc.FileName);
        cmd.Parameters.AddWithValue("$pages", doc.PageCount);
        cmd.Parameters.AddWithValue("$up", doc.UploadedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$status", doc.Status);
        cmd.Parameters.AddWithValue("$committed", doc.GraphCommitted ? 1 : 0);
        cmd.Parameters.AddWithValue("$raw", (object?)rawText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pdf", (object?)pdfPath ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void SetStatus(string docId, string status, bool? graphCommitted = null)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = graphCommitted is null
            ? "UPDATE documents SET status = $s WHERE id = $id"
            : "UPDATE documents SET status = $s, graph_committed = $g WHERE id = $id";
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$id", docId);
        if (graphCommitted is not null) cmd.Parameters.AddWithValue("$g", graphCommitted.Value ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public DocumentRecord? GetDocument(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, title, file_name, page_count, uploaded_utc, status, graph_committed FROM documents WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadDocument(r) : null;
    }

    public IReadOnlyList<DocumentRecord> ListDocuments()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, title, file_name, page_count, uploaded_utc, status, graph_committed FROM documents ORDER BY uploaded_utc DESC";
        using var r = cmd.ExecuteReader();
        var list = new List<DocumentRecord>();
        while (r.Read()) list.Add(ReadDocument(r));
        return list;
    }

    public string? GetRawText(string docId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT raw_text FROM documents WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", docId);
        return cmd.ExecuteScalar() as string;
    }

    public string? GetPdfPath(string docId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pdf_path FROM documents WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", docId);
        return cmd.ExecuteScalar() as string;
    }

    public void DeleteDocument(string docId)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var sql in new[]
                 {
                     "DELETE FROM chunks WHERE doc_id = $id",
                     "DELETE FROM sections WHERE doc_id = $id",
                     "DELETE FROM extraction_results WHERE doc_id = $id",
                     "DELETE FROM triple_rejections WHERE doc_id = $id",
                     "DELETE FROM merge_decisions WHERE doc_id = $id",
                     "DELETE FROM golden_sets WHERE doc_id = $id",
                     "DELETE FROM graph_entities WHERE doc_id = $id",
                     "DELETE FROM graph_edges WHERE doc_id = $id",
                     "DELETE FROM graph_mentions WHERE doc_id = $id",
                     "DELETE FROM vectors WHERE collection LIKE $like",
                     "DELETE FROM documents WHERE id = $id",
                 })
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$id", docId);
            cmd.Parameters.AddWithValue("$like", docId + "_%");
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static DocumentRecord ReadDocument(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Title = r.GetString(1),
        FileName = r.GetString(2),
        PageCount = r.GetInt32(3),
        UploadedUtc = DateTimeOffset.Parse(r.GetString(4)),
        Status = r.GetString(5),
        GraphCommitted = r.GetInt32(6) == 1,
    };

    // ----- sections -------------------------------------------------------

    public void ReplaceSections(string docId, IReadOnlyList<SectionRecord> sections)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM sections WHERE doc_id = $id";
            del.Parameters.AddWithValue("$id", docId);
            del.ExecuteNonQuery();
        }
        foreach (var s in sections)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO sections (id, doc_id, ordinal, heading, start_char, end_char, page,
                                      doctype, subject, year, studio, market, summary, text)
                VALUES ($id, $doc, $ord, $head, $start, $end, $page, $dt, $subj, $year, $studio, $market, $sum, $text)
                """;
            cmd.Parameters.AddWithValue("$id", s.Id);
            cmd.Parameters.AddWithValue("$doc", s.DocId);
            cmd.Parameters.AddWithValue("$ord", s.Ordinal);
            cmd.Parameters.AddWithValue("$head", s.Heading);
            cmd.Parameters.AddWithValue("$start", s.StartChar);
            cmd.Parameters.AddWithValue("$end", s.EndChar);
            cmd.Parameters.AddWithValue("$page", s.Page);
            cmd.Parameters.AddWithValue("$dt", (object?)s.FrontMatter.DocType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$subj", (object?)s.FrontMatter.Subject ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$year", (object?)s.FrontMatter.Year ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$studio", (object?)s.FrontMatter.Studio ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$market", (object?)s.FrontMatter.Market ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sum", (object?)s.Summary ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$text", s.Text);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void SetSectionSummary(string sectionId, string summary)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE sections SET summary = $s WHERE id = $id";
        cmd.Parameters.AddWithValue("$s", summary);
        cmd.Parameters.AddWithValue("$id", sectionId);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<SectionRecord> GetSections(string docId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, doc_id, ordinal, heading, start_char, end_char, page,
                   doctype, subject, year, studio, market, summary, text
            FROM sections WHERE doc_id = $id ORDER BY ordinal
            """;
        cmd.Parameters.AddWithValue("$id", docId);
        using var r = cmd.ExecuteReader();
        var list = new List<SectionRecord>();
        while (r.Read())
        {
            list.Add(new SectionRecord
            {
                Id = r.GetString(0),
                DocId = r.GetString(1),
                Ordinal = r.GetInt32(2),
                Heading = r.GetString(3),
                StartChar = r.GetInt32(4),
                EndChar = r.GetInt32(5),
                Page = r.GetInt32(6),
                FrontMatter = new FrontMatter
                {
                    DocType = r.IsDBNull(7) ? null : r.GetString(7),
                    Subject = r.IsDBNull(8) ? null : r.GetString(8),
                    Year = r.IsDBNull(9) ? null : r.GetInt32(9),
                    Studio = r.IsDBNull(10) ? null : r.GetString(10),
                    Market = r.IsDBNull(11) ? null : r.GetString(11),
                },
                Summary = r.IsDBNull(12) ? null : r.GetString(12),
                Text = r.GetString(13),
            });
        }
        return list;
    }

    // ----- chunks ---------------------------------------------------------

    public void ReplaceChunks(string docId, IReadOnlyList<ChunkRecord> chunks)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM chunks WHERE doc_id = $id";
            del.Parameters.AddWithValue("$id", docId);
            del.ExecuteNonQuery();
        }
        foreach (var c in chunks)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO chunks (id, doc_id, strategy, seq, strategy_ordinal, section_id, page,
                                    start_char, end_char, text, raw_text, doctype, subject, year, studio, market, entity_keys)
                VALUES ($id, $doc, $strat, $seq, $ord, $sec, $page, $start, $end, $text, $raw, $dt, $subj, $year, $studio, $market, $ek)
                """;
            BindChunk(cmd, c);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void SetEntityKeys(IReadOnlyDictionary<string, IReadOnlyList<string>> byChunk)
    {
        if (byChunk.Count == 0) return;
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var (chunkId, keys) in byChunk)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE chunks SET entity_keys = $k WHERE id = $id";
            cmd.Parameters.AddWithValue("$k", JsonSerializer.Serialize(keys, Json));
            cmd.Parameters.AddWithValue("$id", chunkId);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static void BindChunk(SqliteCommand cmd, ChunkRecord c)
    {
        cmd.Parameters.AddWithValue("$id", c.Id);
        cmd.Parameters.AddWithValue("$doc", c.DocId);
        cmd.Parameters.AddWithValue("$strat", c.Strategy);
        cmd.Parameters.AddWithValue("$seq", c.Seq);
        cmd.Parameters.AddWithValue("$ord", c.StrategyOrdinal);
        cmd.Parameters.AddWithValue("$sec", c.SectionId);
        cmd.Parameters.AddWithValue("$page", c.Page);
        cmd.Parameters.AddWithValue("$start", c.StartChar);
        cmd.Parameters.AddWithValue("$end", c.EndChar);
        cmd.Parameters.AddWithValue("$text", c.Text);
        cmd.Parameters.AddWithValue("$raw", c.RawText);
        cmd.Parameters.AddWithValue("$dt", (object?)c.FrontMatter.DocType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$subj", (object?)c.FrontMatter.Subject ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$year", (object?)c.FrontMatter.Year ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$studio", (object?)c.FrontMatter.Studio ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$market", (object?)c.FrontMatter.Market ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ek", JsonSerializer.Serialize(c.EntityKeys, Json));
    }

    public IReadOnlyList<ChunkRecord> GetChunks(string docId, string? strategy = null)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = strategy is null
            ? "SELECT * FROM chunks WHERE doc_id = $id ORDER BY seq"
            : "SELECT * FROM chunks WHERE doc_id = $id AND strategy = $s ORDER BY strategy_ordinal";
        cmd.Parameters.AddWithValue("$id", docId);
        if (strategy is not null) cmd.Parameters.AddWithValue("$s", strategy);
        return ReadChunks(cmd);
    }

    public IReadOnlyList<ChunkRecord> GetChunksByIds(IReadOnlyCollection<string> ids)
    {
        if (ids.Count == 0) return [];
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        var names = ids.Select((_, i) => "$p" + i).ToArray();
        cmd.CommandText = $"SELECT * FROM chunks WHERE id IN ({string.Join(',', names)})";
        var i2 = 0;
        foreach (var id in ids) cmd.Parameters.AddWithValue("$p" + i2++, id);
        return ReadChunks(cmd);
    }

    public ChunkRecord? GetChunk(string id) => GetChunksByIds([id]).FirstOrDefault();

    private static IReadOnlyList<ChunkRecord> ReadChunks(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var cols = Enumerable.Range(0, r.FieldCount).ToDictionary(r.GetName, i => i);
        var list = new List<ChunkRecord>();
        while (r.Read())
        {
            string? Str(string n) => r.IsDBNull(cols[n]) ? null : r.GetString(cols[n]);
            int? Num(string n) => r.IsDBNull(cols[n]) ? null : r.GetInt32(cols[n]);
            list.Add(new ChunkRecord
            {
                Id = r.GetString(cols["id"]),
                DocId = r.GetString(cols["doc_id"]),
                Strategy = r.GetString(cols["strategy"]),
                Seq = r.GetInt32(cols["seq"]),
                StrategyOrdinal = r.GetInt32(cols["strategy_ordinal"]),
                SectionId = r.GetString(cols["section_id"]),
                Page = r.GetInt32(cols["page"]),
                StartChar = r.GetInt32(cols["start_char"]),
                EndChar = r.GetInt32(cols["end_char"]),
                Text = r.GetString(cols["text"]),
                RawText = r.GetString(cols["raw_text"]),
                FrontMatter = new FrontMatter
                {
                    DocType = Str("doctype"), Subject = Str("subject"), Year = Num("year"),
                    Studio = Str("studio"), Market = Str("market"),
                },
                EntityKeys = JsonSerializer.Deserialize<List<string>>(r.GetString(cols["entity_keys"]), Json) ?? [],
            });
        }
        return list;
    }

    /// <summary>
    /// Maps chunk ids from one strategy onto the strategy the graph was extracted from, by
    /// character-span overlap. This is what lets stage-10 expansion work from any collection.
    /// </summary>
    public IReadOnlyList<string> MapToStrategy(string docId, IEnumerable<string> chunkIds, string targetStrategy)
    {
        var seeds = GetChunksByIds(chunkIds.ToList());
        var direct = seeds.Where(c => c.Strategy == targetStrategy).Select(c => c.Id).ToList();
        var needsMapping = seeds.Where(c => c.Strategy != targetStrategy).ToList();
        if (needsMapping.Count == 0) return direct;

        var target = GetChunks(docId, targetStrategy);
        var mapped = new List<string>(direct);
        foreach (var seed in needsMapping)
        {
            var overlapping = target
                .Where(t => t.StartChar < seed.EndChar && t.EndChar > seed.StartChar)
                .OrderByDescending(t => Math.Min(t.EndChar, seed.EndChar) - Math.Max(t.StartChar, seed.StartChar))
                .Take(2)
                .Select(t => t.Id);
            mapped.AddRange(overlapping);
        }
        return mapped.Distinct().ToList();
    }
}
