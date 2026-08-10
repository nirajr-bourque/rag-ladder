using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using RagLadder.Api.Configuration;

namespace RagLadder.Api.Infrastructure;

/// <summary>
/// Owns the SQLite file: connection factory plus schema creation. Everything durable that is not
/// in Qdrant or Neo4j lives here — documents, sections, chunks, caches, review state, eval runs,
/// and (for the local providers) the vectors and the graph itself.
/// </summary>
public sealed class Db
{
    private readonly string _connectionString;

    public Db(IOptions<RagLadderOptions> options)
    {
        var storage = options.Value.Storage;
        Directory.CreateDirectory(storage.DataDirectory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = storage.SqlitePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
        Initialize();
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Schema;
        cmd.ExecuteNonQuery();
    }

    private const string Schema = """
        PRAGMA journal_mode = WAL;

        CREATE TABLE IF NOT EXISTS documents (
            id              TEXT PRIMARY KEY,
            title           TEXT NOT NULL,
            file_name       TEXT NOT NULL,
            page_count      INTEGER NOT NULL,
            uploaded_utc    TEXT NOT NULL,
            status          TEXT NOT NULL,
            graph_committed INTEGER NOT NULL DEFAULT 0,
            raw_text        TEXT,
            pdf_path        TEXT
        );

        CREATE TABLE IF NOT EXISTS sections (
            id         TEXT PRIMARY KEY,
            doc_id     TEXT NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
            ordinal    INTEGER NOT NULL,
            heading    TEXT NOT NULL,
            start_char INTEGER NOT NULL,
            end_char   INTEGER NOT NULL,
            page       INTEGER NOT NULL,
            doctype    TEXT, subject TEXT, year INTEGER, studio TEXT, market TEXT,
            summary    TEXT,
            text       TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_sections_doc ON sections(doc_id, ordinal);

        CREATE TABLE IF NOT EXISTS chunks (
            id               TEXT PRIMARY KEY,
            doc_id           TEXT NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
            strategy         TEXT NOT NULL,
            seq              INTEGER NOT NULL,
            strategy_ordinal INTEGER NOT NULL,
            section_id       TEXT NOT NULL,
            page             INTEGER NOT NULL,
            start_char       INTEGER NOT NULL,
            end_char         INTEGER NOT NULL,
            text             TEXT NOT NULL,
            raw_text         TEXT NOT NULL,
            doctype TEXT, subject TEXT, year INTEGER, studio TEXT, market TEXT,
            entity_keys      TEXT NOT NULL DEFAULT '[]'
        );
        CREATE INDEX IF NOT EXISTS ix_chunks_doc_strategy ON chunks(doc_id, strategy, strategy_ordinal);
        CREATE INDEX IF NOT EXISTS ix_chunks_span ON chunks(doc_id, start_char, end_char);

        -- Content-hash caches. Reprocessing an unchanged document must cost zero model calls.
        CREATE TABLE IF NOT EXISTS embedding_cache (
            hash     TEXT PRIMARY KEY,
            model_id TEXT NOT NULL,
            dim      INTEGER NOT NULL,
            vector   BLOB NOT NULL
        );
        CREATE TABLE IF NOT EXISTS extraction_cache (
            hash         TEXT PRIMARY KEY,
            payload      TEXT NOT NULL,
            created_utc  TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS chat_cache (
            hash         TEXT PRIMARY KEY,
            purpose      TEXT NOT NULL,
            response     TEXT NOT NULL,
            created_utc  TEXT NOT NULL
        );
        -- Whole-answer cache. chat_cache spares the model call but retrieval still re-runs, which
        -- on a CPU box is most of the wall clock. This stores the entire response envelope against
        -- the stage-scoped key, so a repeat question replays instantly and survives a restart —
        -- the difference between a demo that warms up once and one that warms up every morning.
        -- Bounded by LRU on last_used_utc; see CacheRepository.AnswerCacheLimit.
        CREATE TABLE IF NOT EXISTS answer_cache (
            hash          TEXT PRIMARY KEY,
            doc_id        TEXT NOT NULL,
            question      TEXT NOT NULL,
            stage         INTEGER,
            payload       TEXT NOT NULL,
            created_utc   TEXT NOT NULL,
            last_used_utc TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_answer_cache_lru ON answer_cache(last_used_utc);

        -- Proposed graph awaiting the human review gate.
        CREATE TABLE IF NOT EXISTS extraction_results (
            doc_id  TEXT PRIMARY KEY REFERENCES documents(id) ON DELETE CASCADE,
            payload TEXT NOT NULL
        );
        -- Rejections persist by triple hash so reprocessing does not resurface them (spec §6.7).
        CREATE TABLE IF NOT EXISTS triple_rejections (
            doc_id      TEXT NOT NULL,
            triple_hash TEXT NOT NULL,
            PRIMARY KEY (doc_id, triple_hash)
        );
        CREATE TABLE IF NOT EXISTS merge_decisions (
            doc_id    TEXT NOT NULL,
            left_key  TEXT NOT NULL,
            right_key TEXT NOT NULL,
            decision  TEXT NOT NULL,
            PRIMARY KEY (doc_id, left_key, right_key)
        );

        CREATE TABLE IF NOT EXISTS golden_sets (
            doc_id  TEXT PRIMARY KEY,
            payload TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS eval_runs (
            run_id  TEXT PRIMARY KEY,
            doc_id  TEXT NOT NULL,
            started TEXT NOT NULL,
            payload TEXT NOT NULL
        );

        -- Local vector provider.
        CREATE TABLE IF NOT EXISTS vectors (
            collection TEXT NOT NULL,
            chunk_id   TEXT NOT NULL,
            vector     BLOB NOT NULL,
            payload    TEXT NOT NULL,
            PRIMARY KEY (collection, chunk_id)
        );

        -- Local graph provider.
        CREATE TABLE IF NOT EXISTS graph_entities (
            doc_id  TEXT NOT NULL,
            key     TEXT NOT NULL,
            type    TEXT NOT NULL,
            name    TEXT NOT NULL,
            year    INTEGER,
            mentions INTEGER NOT NULL DEFAULT 0,
            aliases TEXT NOT NULL DEFAULT '[]',
            PRIMARY KEY (doc_id, key)
        );
        CREATE TABLE IF NOT EXISTS graph_edges (
            doc_id     TEXT NOT NULL,
            from_key   TEXT NOT NULL,
            to_key     TEXT NOT NULL,
            predicate  TEXT NOT NULL,
            confidence REAL NOT NULL,
            mentions   INTEGER NOT NULL DEFAULT 1,
            derived    INTEGER NOT NULL DEFAULT 0,
            flipped    INTEGER NOT NULL DEFAULT 0,
            evidence   TEXT,
            verdict    TEXT,
            verdict_reason TEXT,
            chunk_ids  TEXT NOT NULL DEFAULT '[]',
            properties TEXT NOT NULL DEFAULT '{}',
            PRIMARY KEY (doc_id, from_key, predicate, to_key)
        );
        CREATE INDEX IF NOT EXISTS ix_edges_from ON graph_edges(doc_id, from_key);
        CREATE INDEX IF NOT EXISTS ix_edges_to ON graph_edges(doc_id, to_key);
        CREATE TABLE IF NOT EXISTS graph_mentions (
            doc_id     TEXT NOT NULL,
            chunk_id   TEXT NOT NULL,
            entity_key TEXT NOT NULL,
            PRIMARY KEY (doc_id, chunk_id, entity_key)
        );
        CREATE INDEX IF NOT EXISTS ix_mentions_entity ON graph_mentions(doc_id, entity_key);
        """;
}
