using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using RagLadder.Api.Configuration;
using RagLadder.Api.Infrastructure;
using RagLadder.Api.Llm;
using RagLadder.Api.Models;

namespace RagLadder.Api.Graph;

/// <summary>
/// SQLite-backed graph store used when Neo4j AuraDB is not configured. Traversal runs in memory:
/// with the corpus capped at roughly 200 people and 80 titles, a breadth-first search over the
/// credit edges is instant, and the demo keeps working with no hosted dependency.
/// Semantics deliberately mirror the Cypher in <see cref="AggregationCypher"/>.
/// </summary>
public sealed class LocalGraphStore(Db db, CorpusRepository corpus, IOptions<RagLadderOptions> options) : IGraphStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _extractionStrategy = options.Value.Extraction.SourceStrategy;

    public string Kind => "local-sqlite";

    public Task EnsureSchemaAsync(CancellationToken ct = default) => Task.CompletedTask;

    // ----- commit ---------------------------------------------------------

    public Task CommitAsync(GraphCommit commit, CancellationToken ct = default)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var docId = commit.Document.Id;

        foreach (var sql in new[] { "DELETE FROM graph_entities WHERE doc_id=$d", "DELETE FROM graph_edges WHERE doc_id=$d", "DELETE FROM graph_mentions WHERE doc_id=$d" })
        {
            using var del = conn.CreateCommand();
            del.Transaction = tx;
            del.CommandText = sql;
            del.Parameters.AddWithValue("$d", docId);
            del.ExecuteNonQuery();
        }

        foreach (var e in commit.Entities)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO graph_entities (doc_id, key, type, name, year, mentions, aliases)
                VALUES ($d, $k, $t, $n, $y, $m, $a)
                ON CONFLICT(doc_id, key) DO UPDATE SET
                    name = excluded.name, mentions = excluded.mentions, aliases = excluded.aliases
                """;
            cmd.Parameters.AddWithValue("$d", docId);
            cmd.Parameters.AddWithValue("$k", e.Key);
            cmd.Parameters.AddWithValue("$t", e.Type);
            cmd.Parameters.AddWithValue("$n", e.Name);
            cmd.Parameters.AddWithValue("$y", (object?)e.Year ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$m", e.MentionCount);
            cmd.Parameters.AddWithValue("$a", JsonSerializer.Serialize(e.Aliases, Json));
            cmd.ExecuteNonQuery();

            foreach (var chunkId in e.ChunkIds)
            {
                using var mention = conn.CreateCommand();
                mention.Transaction = tx;
                mention.CommandText = "INSERT INTO graph_mentions (doc_id, chunk_id, entity_key) VALUES ($d,$c,$k) ON CONFLICT DO NOTHING";
                mention.Parameters.AddWithValue("$d", docId);
                mention.Parameters.AddWithValue("$c", chunkId);
                mention.Parameters.AddWithValue("$k", e.Key);
                mention.ExecuteNonQuery();
            }
        }

        foreach (var r in commit.Relations)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO graph_edges (doc_id, from_key, to_key, predicate, confidence, mentions, derived,
                                         flipped, evidence, verdict, verdict_reason, chunk_ids, properties)
                VALUES ($d,$f,$t,$p,$c,$m,0,$fl,$e,$v,$vr,$ci,$pr)
                ON CONFLICT(doc_id, from_key, predicate, to_key) DO UPDATE SET
                    confidence = MAX(graph_edges.confidence, excluded.confidence),
                    mentions = graph_edges.mentions + excluded.mentions
                """;
            cmd.Parameters.AddWithValue("$d", docId);
            cmd.Parameters.AddWithValue("$f", r.SubjectKey);
            cmd.Parameters.AddWithValue("$t", r.ObjectKey);
            cmd.Parameters.AddWithValue("$p", r.Predicate);
            cmd.Parameters.AddWithValue("$c", r.Confidence);
            cmd.Parameters.AddWithValue("$m", r.MentionCount);
            cmd.Parameters.AddWithValue("$fl", r.Flipped ? 1 : 0);
            cmd.Parameters.AddWithValue("$e", (object?)r.Evidence ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$v", (object?)r.Verdict ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$vr", (object?)r.VerdictReason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ci", JsonSerializer.Serialize(r.ChunkIds, Json));
            cmd.Parameters.AddWithValue("$pr", JsonSerializer.Serialize(r.Properties, Json));
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return Task.CompletedTask;
    }

    /// <summary>Mirrors the Cypher in spec §6.9: two people who worked on the same title.</summary>
    public Task<int> ComputeDerivedEdgesAsync(string docId, CancellationToken ct = default)
    {
        var edges = LoadEdges(docId, 0, includeDerived: false);
        var entities = LoadEntities(docId);

        var worksByPerson = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var e in edges.Where(e => Ontology.CreditPredicates.Contains(e.Predicate)))
        {
            // SHOT_BY and EDITED_BY run work → person; the rest run person → work.
            var (person, work) = e.Predicate is "SHOT_BY" or "EDITED_BY" ? (e.To, e.From) : (e.From, e.To);
            if (EntityKey.TypeOfKey(person) != "Person") continue;
            if (!worksByPerson.TryGetValue(person, out var set)) worksByPerson[person] = set = [];
            set.Add(work);
        }

        var people = worksByPerson.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var derived = new List<(string A, string B, int Shared, List<string> Titles)>();
        for (var i = 0; i < people.Length; i++)
        for (var j = i + 1; j < people.Length; j++)
        {
            var shared = worksByPerson[people[i]].Intersect(worksByPerson[people[j]], StringComparer.Ordinal).ToList();
            if (shared.Count == 0) continue;
            var titles = shared.Take(5)
                .Select(k => entities.TryGetValue(k, out var w) ? w.Name : k)
                .ToList();
            derived.Add((people[i], people[j], shared.Count, titles));
        }

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM graph_edges WHERE doc_id = $d AND derived = 1";
            del.Parameters.AddWithValue("$d", docId);
            del.ExecuteNonQuery();
        }

        foreach (var (a, b, shared, titles) in derived)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO graph_edges (doc_id, from_key, to_key, predicate, confidence, mentions, derived, properties)
                VALUES ($d, $a, $b, 'COLLABORATED_WITH', 1.0, $n, 1, $props)
                ON CONFLICT(doc_id, from_key, predicate, to_key) DO UPDATE SET
                    mentions = excluded.mentions, properties = excluded.properties, derived = 1
                """;
            cmd.Parameters.AddWithValue("$d", docId);
            cmd.Parameters.AddWithValue("$a", a);
            cmd.Parameters.AddWithValue("$b", b);
            cmd.Parameters.AddWithValue("$n", shared);
            cmd.Parameters.AddWithValue("$props", JsonSerializer.Serialize(
                new Dictionary<string, string> { ["count"] = shared.ToString(), ["titles"] = string.Join(" · ", titles) }, Json));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return Task.FromResult(derived.Count);
    }

    // ----- expand ---------------------------------------------------------

    public Task<ExpandResult> ExpandAsync(string docId, IReadOnlyList<string> chunkIds, GraphHops hops,
        double minConfidence, bool includeDerived, CancellationToken ct = default)
    {
        var entities = LoadEntities(docId);
        var edges = LoadEdges(docId, minConfidence, includeDerived);
        var mentions = LoadMentions(docId);
        var chunksByEntity = mentions
            .GroupBy(m => m.EntityKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(m => m.ChunkId).ToList(), StringComparer.Ordinal);
        var entitiesByChunk = mentions
            .GroupBy(m => m.ChunkId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(m => m.EntityKey).ToList(), StringComparer.Ordinal);

        var strategyChunks = corpus.GetChunks(docId, _extractionStrategy);
        var byId = strategyChunks.ToDictionary(c => c.Id, c => c, StringComparer.Ordinal);
        var byOrdinal = strategyChunks.ToDictionary(c => c.StrategyOrdinal, c => c);
        var sections = corpus.GetSections(docId).ToDictionary(s => s.Id, s => s, StringComparer.Ordinal);

        var expanded = new List<ExpandedChunk>();
        var touched = new Dictionary<string, GraphEntity>(StringComparer.Ordinal);
        var traversed = new List<GraphEdge>();

        foreach (var id in chunkIds.Distinct(StringComparer.Ordinal))
        {
            if (!byId.TryGetValue(id, out var chunk)) continue;

            ChunkRecord? prev = null, next = null;
            if (hops.Next)
            {
                byOrdinal.TryGetValue(chunk.StrategyOrdinal - 1, out prev);
                byOrdinal.TryGetValue(chunk.StrategyOrdinal + 1, out next);
            }

            var chunkEntities = new List<GraphEntity>();
            var related = new List<GraphEdge>();
            var relatedChunks = new List<string>();

            if (hops.Entity && entitiesByChunk.TryGetValue(id, out var keys))
            {
                foreach (var key in keys)
                {
                    if (!entities.TryGetValue(key, out var entity)) continue;
                    chunkEntities.Add(entity);
                    touched[key] = entity;

                    if (!hops.EntityRel) continue;
                    foreach (var edge in edges.Where(e => e.From == key || e.To == key))
                    {
                        related.Add(edge);
                        traversed.Add(edge);
                        var other = edge.From == key ? edge.To : edge.From;
                        if (entities.TryGetValue(other, out var otherEntity)) touched[other] = otherEntity;
                        if (chunksByEntity.TryGetValue(other, out var viaChunks))
                            relatedChunks.AddRange(viaChunks.Where(c => c != id));
                    }
                }
            }

            var summary = hops.Parent && sections.TryGetValue(chunk.SectionId, out var section) ? section.Summary : null;

            expanded.Add(new ExpandedChunk
            {
                Id = id,
                Text = chunk.RawText,
                PrevText = prev?.RawText,
                NextText = next?.RawText,
                PrevId = prev?.Id,
                NextId = next?.Id,
                SectionSummary = summary,
                Entities = chunkEntities,
                Related = [.. related.DistinctBy(e => (e.From, e.Predicate, e.To))],
                RelatedChunkIds = [.. relatedChunks.Distinct(StringComparer.Ordinal).Take(6)],
            });
        }

        return Task.FromResult(new ExpandResult
        {
            Chunks = expanded,
            EntitiesTouched = [.. touched.Values],
            EdgesTraversed = [.. traversed.DistinctBy(e => (e.From, e.Predicate, e.To))],
        });
    }

    // ----- shortest path --------------------------------------------------

    public Task<PathResult?> ShortestPathAsync(string docId, string fromKey, string toKey, int maxHops,
        double minConfidence, CancellationToken ct = default)
    {
        var entities = LoadEntities(docId);
        if (!entities.ContainsKey(fromKey) || !entities.ContainsKey(toKey)) return Task.FromResult<PathResult?>(null);
        if (fromKey == toKey) return Task.FromResult<PathResult?>(null);

        var edges = LoadEdges(docId, minConfidence, includeDerived: false)
            .Where(e => Ontology.CreditPredicates.Contains(e.Predicate))
            .ToList();

        var adjacency = new Dictionary<string, List<(string Neighbour, string Predicate)>>(StringComparer.Ordinal);
        foreach (var e in edges)
        {
            if (!adjacency.TryGetValue(e.From, out var a)) adjacency[e.From] = a = [];
            a.Add((e.To, e.Predicate));
            if (!adjacency.TryGetValue(e.To, out var b)) adjacency[e.To] = b = [];
            b.Add((e.From, e.Predicate));
        }

        // Breadth-first search gives the same guarantee as shortestPath: the fewest hops.
        var previous = new Dictionary<string, (string Node, string Predicate)>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal) { fromKey };
        var queue = new Queue<(string Node, int Depth)>();
        queue.Enqueue((fromKey, 0));
        var found = false;

        while (queue.Count > 0 && !found)
        {
            var (node, depth) = queue.Dequeue();
            if (depth >= maxHops) continue;
            if (!adjacency.TryGetValue(node, out var neighbours)) continue;

            foreach (var (neighbour, predicate) in neighbours.OrderBy(n => n.Neighbour, StringComparer.Ordinal))
            {
                if (!visited.Add(neighbour)) continue;
                previous[neighbour] = (node, predicate);
                if (neighbour == toKey) { found = true; break; }
                queue.Enqueue((neighbour, depth + 1));
            }
        }

        if (!found) return Task.FromResult<PathResult?>(null);

        var nodeKeys = new List<string> { toKey };
        var rels = new List<string>();
        var cursor = toKey;
        while (cursor != fromKey)
        {
            var (prevNode, predicate) = previous[cursor];
            rels.Insert(0, predicate);
            nodeKeys.Insert(0, prevNode);
            cursor = prevNode;
        }

        var nodes = nodeKeys.Select(k => new PathNode
        {
            Key = k,
            Name = entities[k].Name,
            Type = entities[k].Type,
            Year = entities[k].Year
        }).ToList();

        var ontology = Ontology.Default();
        return Task.FromResult<PathResult?>(new PathResult
        {
            Hops = rels.Count,
            Nodes = nodes,
            Rels = rels,
            Narrative = PathNarrative.Render(nodes, rels, ontology)
        });
    }

    // ----- aggregation ----------------------------------------------------

    public Task<AggregationResult> AggregateAsync(string docId, string presetId, int? year, double minConfidence, CancellationToken ct = default)
    {
        var entities = LoadEntities(docId);
        var edges = LoadEdges(docId, minConfidence, includeDerived: false);
        string Name(string key) => entities.TryGetValue(key, out var e) ? e.Name : key;
        int? Year(string key) => entities.TryGetValue(key, out var e) ? e.Year : null;

        List<AggregationRow> rows;
        string[] columns;

        switch (presetId)
        {
            case AggregationPresets.StudioFilmCount:
            {
                columns = ["studio", "films", "titles"];
                rows = edges.Where(e => e.Predicate == "PRODUCED_BY")
                    .Where(e => year is null || Year(e.From) == year)
                    .GroupBy(e => e.To, StringComparer.Ordinal)
                    .Select(g => new AggregationRow(new Dictionary<string, object?>
                    {
                        ["studio"] = Name(g.Key),
                        ["films"] = g.Select(x => x.From).Distinct(StringComparer.Ordinal).Count(),
                        ["titles"] = string.Join(" · ", g.Select(x => Name(x.From)).Distinct().Take(8))
                    }))
                    .OrderByDescending(r => (int)r.Values["films"]!)
                    .ToList();
                break;
            }
            case AggregationPresets.DirectorCinematographerPairs:
            {
                columns = ["director", "cinematographer", "films", "titles"];
                var directed = edges.Where(e => e.Predicate == "DIRECTED")
                    .GroupBy(e => e.To, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Select(x => x.From).ToList(), StringComparer.Ordinal);
                var shot = edges.Where(e => e.Predicate == "SHOT_BY").ToList();

                rows = shot
                    .SelectMany(s => directed.TryGetValue(s.From, out var directors)
                        ? directors.Select(d => (Director: d, Cinematographer: s.To, Film: s.From))
                        : [])
                    .GroupBy(x => (x.Director, x.Cinematographer))
                    .Select(g => new AggregationRow(new Dictionary<string, object?>
                    {
                        ["director"] = Name(g.Key.Director),
                        ["cinematographer"] = Name(g.Key.Cinematographer),
                        ["films"] = g.Select(x => x.Film).Distinct(StringComparer.Ordinal).Count(),
                        ["titles"] = string.Join(" · ", g.Select(x => Name(x.Film)).Distinct())
                    }))
                    .OrderByDescending(r => (int)r.Values["films"]!)
                    .ToList();
                break;
            }
            case AggregationPresets.MultiFranchiseActors:
            {
                columns = ["person", "franchises", "names"];
                var franchiseOf = edges.Where(e => e.Predicate == "PART_OF_FRANCHISE")
                    .GroupBy(e => e.From, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Select(x => x.To).ToList(), StringComparer.Ordinal);

                rows = edges.Where(e => e.Predicate == "ACTED_IN")
                    .SelectMany(e => franchiseOf.TryGetValue(e.To, out var fs)
                        ? fs.Select(f => (Person: e.From, Franchise: f))
                        : [])
                    .GroupBy(x => x.Person, StringComparer.Ordinal)
                    .Select(g => new
                    {
                        Person = g.Key,
                        Franchises = g.Select(x => x.Franchise).Distinct(StringComparer.Ordinal).ToList()
                    })
                    .Where(x => x.Franchises.Count > 1)
                    .Select(x => new AggregationRow(new Dictionary<string, object?>
                    {
                        ["person"] = Name(x.Person),
                        ["franchises"] = x.Franchises.Count,
                        ["names"] = string.Join(" · ", x.Franchises.Select(Name))
                    }))
                    .OrderByDescending(r => (int)r.Values["franchises"]!)
                    .ToList();
                break;
            }
            case AggregationPresets.AwardTallyByStudio:
            {
                columns = ["studio", "wins", "titles"];
                var studioOf = edges.Where(e => e.Predicate == "PRODUCED_BY")
                    .GroupBy(e => e.From, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First().To, StringComparer.Ordinal);

                rows = edges.Where(e => e.Predicate == "WON")
                    .Where(e => studioOf.ContainsKey(e.From))
                    .GroupBy(e => studioOf[e.From], StringComparer.Ordinal)
                    .Select(g => new AggregationRow(new Dictionary<string, object?>
                    {
                        ["studio"] = Name(g.Key),
                        ["wins"] = g.Count(),
                        ["titles"] = string.Join(" · ", g.Select(x => Name(x.From)).Distinct().Take(8))
                    }))
                    .OrderByDescending(r => (int)r.Values["wins"]!)
                    .ToList();
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(presetId), presetId, "Unknown aggregation preset.");
        }

        return Task.FromResult(new AggregationResult
        {
            PresetId = presetId,
            Title = AggregationCypher.TitleFor(presetId),
            Cypher = AggregationCypher.For(presetId),
            Columns = columns,
            Rows = rows,
        });
    }

    // ----- inspection -----------------------------------------------------

    public Task<GraphSnapshot> SnapshotAsync(string docId, double minConfidence, bool includeDerived, int limit, CancellationToken ct = default)
    {
        var entities = LoadEntities(docId);
        var edges = LoadEdges(docId, minConfidence, includeDerived);
        var trimmed = edges.Take(limit).ToList();
        var keep = trimmed.SelectMany(e => new[] { e.From, e.To }).ToHashSet(StringComparer.Ordinal);
        var nodes = entities.Values.Where(e => keep.Contains(e.Key) || edges.Count <= limit).Take(limit).ToList();

        return Task.FromResult(new GraphSnapshot
        {
            Nodes = nodes,
            Edges = trimmed,
            TotalNodes = entities.Count,
            TotalEdges = edges.Count,
            Truncated = edges.Count > trimmed.Count,
        });
    }

    public Task<IReadOnlyList<EntityRef>> SearchEntitiesAsync(string docId, string? type, string? query, int limit, CancellationToken ct = default)
    {
        var entities = LoadEntities(docId).Values
            .Where(e => type is null || string.Equals(e.Type, type, StringComparison.OrdinalIgnoreCase))
            .Where(e => query is null || e.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.MentionCount)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(e => new EntityRef { Key = e.Key, Name = e.Name, Type = e.Type, MentionCount = e.MentionCount, Year = e.Year })
            .ToList();
        return Task.FromResult<IReadOnlyList<EntityRef>>(entities);
    }

    public Task<GraphEdge?> GetEdgeAsync(string docId, string fromKey, string predicate, string toKey, CancellationToken ct = default)
    {
        var edge = LoadEdges(docId, 0, includeDerived: true)
            .FirstOrDefault(e => e.From == fromKey && e.To == toKey && e.Predicate == predicate);
        return Task.FromResult(edge);
    }

    public Task DeleteDocumentAsync(string docId, CancellationToken ct = default)
    {
        using var conn = db.Open();
        foreach (var table in new[] { "graph_entities", "graph_edges", "graph_mentions" })
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM {table} WHERE doc_id = $d";
            cmd.Parameters.AddWithValue("$d", docId);
            cmd.ExecuteNonQuery();
        }
        return Task.CompletedTask;
    }

    public Task<ProviderHealth> HealthAsync(CancellationToken ct = default)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT (SELECT COUNT(*) FROM graph_entities), (SELECT COUNT(*) FROM graph_edges)";
        using var r = cmd.ExecuteReader();
        var (nodes, edges) = r.Read() ? (r.GetInt32(0), r.GetInt32(1)) : (0, 0);
        return Task.FromResult(new ProviderHealth("graph", ProviderHealth.Ok,
            $"Local SQLite graph: {nodes} entities, {edges} edges. Neo4j AuraDB not configured."));
    }

    // ----- loading --------------------------------------------------------

    private Dictionary<string, GraphEntity> LoadEntities(string docId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key, type, name, year, mentions, aliases FROM graph_entities WHERE doc_id = $d";
        cmd.Parameters.AddWithValue("$d", docId);
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, GraphEntity>(StringComparer.Ordinal);
        while (r.Read())
        {
            map[r.GetString(0)] = new GraphEntity
            {
                Key = r.GetString(0),
                Type = r.GetString(1),
                Name = r.GetString(2),
                Year = r.IsDBNull(3) ? null : r.GetInt32(3),
                MentionCount = r.GetInt32(4),
                Aliases = JsonSerializer.Deserialize<List<string>>(r.GetString(5), Json) ?? [],
            };
        }
        return map;
    }

    private List<GraphEdge> LoadEdges(string docId, double minConfidence, bool includeDerived)
    {
        var entities = _entityNameCache.TryGetValue(docId, out var cached) ? cached : null;
        entities ??= LoadEntities(docId).ToDictionary(e => e.Key, e => e.Value.Name, StringComparer.Ordinal);
        _entityNameCache[docId] = entities;

        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT from_key, to_key, predicate, confidence, mentions, derived, flipped,
                   evidence, verdict, verdict_reason, chunk_ids, properties
            FROM graph_edges
            WHERE doc_id = $d AND confidence >= $c AND ($derived = 1 OR derived = 0)
            """;
        cmd.Parameters.AddWithValue("$d", docId);
        cmd.Parameters.AddWithValue("$c", minConfidence);
        cmd.Parameters.AddWithValue("$derived", includeDerived ? 1 : 0);
        using var r = cmd.ExecuteReader();

        var list = new List<GraphEdge>();
        while (r.Read())
        {
            var derived = r.GetInt32(5) == 1;
            if (derived && !includeDerived) continue;
            list.Add(ReadEdge(r, entities, derived));
        }
        return list;
    }

    private readonly Dictionary<string, Dictionary<string, string>> _entityNameCache = new(StringComparer.Ordinal);

    private static GraphEdge ReadEdge(SqliteDataReader r, IReadOnlyDictionary<string, string> names, bool derived) => new()
    {
        From = r.GetString(0),
        To = r.GetString(1),
        FromName = names.GetValueOrDefault(r.GetString(0), r.GetString(0)),
        ToName = names.GetValueOrDefault(r.GetString(1), r.GetString(1)),
        Predicate = r.GetString(2),
        Confidence = r.GetDouble(3),
        MentionCount = r.GetInt32(4),
        Derived = derived,
        Flipped = r.GetInt32(6) == 1,
        Evidence = r.IsDBNull(7) ? null : r.GetString(7),
        Verdict = r.IsDBNull(8) ? null : r.GetString(8),
        VerdictReason = r.IsDBNull(9) ? null : r.GetString(9),
        ChunkIds = JsonSerializer.Deserialize<List<string>>(r.GetString(10), Json) ?? [],
        Properties = JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(11), Json) ?? [],
    };

    private List<(string ChunkId, string EntityKey)> LoadMentions(string docId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT chunk_id, entity_key FROM graph_mentions WHERE doc_id = $d";
        cmd.Parameters.AddWithValue("$d", docId);
        using var r = cmd.ExecuteReader();
        var list = new List<(string, string)>();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }
}
