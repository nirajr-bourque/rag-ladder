using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Neo4j.Driver;
using RagLadder.Api.Configuration;
using RagLadder.Api.Llm;
using RagLadder.Api.Models;

namespace RagLadder.Api.Graph;

/// <summary>
/// Neo4j AuraDB. Relationships are written with the predicate as the relationship <em>type</em>
/// (and also as an <c>r.predicate</c> property) so the traversal and aggregation Cypher in the
/// spec runs verbatim. Labels and types are interpolated into the query text, which is safe only
/// because both are validated against the closed ontology first — see <see cref="SafeIdentifier"/>.
/// </summary>
public sealed partial class Neo4jGraphStore : IGraphStore, IAsyncDisposable
{
    private readonly IDriver _driver;
    private readonly string _database;
    private readonly int _batchSize;
    private readonly Ontology _ontology;
    private readonly ILogger<Neo4jGraphStore> _log;

    public string Kind => "neo4j";

    public Neo4jGraphStore(IOptions<RagLadderOptions> options, Ontology ontology, ILogger<Neo4jGraphStore> log)
    {
        var cfg = options.Value.Neo4j;
        _database = cfg.Database;
        _batchSize = options.Value.Chunking.CommitBatchSize;
        _ontology = ontology;
        _log = log;
        _driver = GraphDatabase.Driver(cfg.Uri, AuthTokens.Basic(cfg.User, cfg.Password));
    }

    private IAsyncSession Session() => _driver.AsyncSession(o => o.WithDatabase(_database));

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        var statements = new[]
        {
            "CREATE CONSTRAINT chunk_id   IF NOT EXISTS FOR (c:Chunk)     REQUIRE c.id  IS UNIQUE",
            "CREATE CONSTRAINT film_key   IF NOT EXISTS FOR (f:Film)      REQUIRE f.key IS UNIQUE",
            "CREATE CONSTRAINT person_key IF NOT EXISTS FOR (p:Person)    REQUIRE p.key IS UNIQUE",
            "CREATE CONSTRAINT char_key   IF NOT EXISTS FOR (c:Character) REQUIRE c.key IS UNIQUE",
            "CREATE CONSTRAINT series_key IF NOT EXISTS FOR (s:TVSeries)  REQUIRE s.key IS UNIQUE",
            "CREATE CONSTRAINT studio_key IF NOT EXISTS FOR (s:Studio)    REQUIRE s.key IS UNIQUE",
            "CREATE INDEX film_title      IF NOT EXISTS FOR (f:Film)      ON (f.title)",
            "CREATE INDEX person_name     IF NOT EXISTS FOR (p:Person)    ON (p.name)",
            "CREATE INDEX chunk_doc       IF NOT EXISTS FOR (c:Chunk)     ON (c.docId)",
        };
        await using var session = Session();
        foreach (var statement in statements)
            await session.RunAsync(statement).ContinueWith(t => t.Result.ConsumeAsync(), ct).Unwrap();
    }

    // ----- commit ---------------------------------------------------------

    public async Task CommitAsync(GraphCommit commit, CancellationToken ct = default)
    {
        var docId = commit.Document.Id;
        await using var session = Session();

        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("""
                MERGE (d:Document {id: $id})
                SET d.title = $title, d.pageCount = $pages, d.uploadedUtc = $uploaded
                """, new { id = docId, title = commit.Document.Title, pages = commit.Document.PageCount, uploaded = commit.Document.UploadedUtc.ToString("O") });
            return true;
        });

        // Structural nodes and edges: written by code, exact and free (spec §6.1).
        foreach (var batch in commit.Sections.Chunk(_batchSize))
        {
            var rows = batch.Select(s => new Dictionary<string, object?>
            {
                ["id"] = s.Id, ["docId"] = docId, ["docType"] = s.FrontMatter.DocType,
                ["subject"] = s.FrontMatter.Subject, ["year"] = s.FrontMatter.Year,
                ["studio"] = s.FrontMatter.Studio, ["market"] = s.FrontMatter.Market,
                ["page"] = s.Page, ["heading"] = s.Heading, ["summary"] = s.Summary
            }).ToList();

            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync("""
                    UNWIND $rows AS row
                    MERGE (s:Section {id: row.id})
                    SET s += row
                    WITH s, row
                    MATCH (d:Document {id: row.docId})
                    MERGE (s)-[:PART_OF]->(d)
                    """, new { rows });
                return true;
            });
        }

        foreach (var batch in commit.Chunks.Chunk(_batchSize))
        {
            var rows = batch.Select(c => new Dictionary<string, object?>
            {
                ["id"] = c.Id, ["docId"] = docId, ["text"] = c.RawText, ["seq"] = c.Seq,
                ["page"] = c.Page, ["section"] = c.SectionId, ["strategy"] = c.Strategy,
                ["ordinal"] = c.StrategyOrdinal
            }).ToList();

            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync("""
                    UNWIND $rows AS row
                    MERGE (c:Chunk {id: row.id})
                    SET c += row
                    WITH c, row
                    MATCH (d:Document {id: row.docId})
                    MERGE (c)-[:PART_OF]->(d)
                    WITH c, row
                    MATCH (s:Section {id: row.section})
                    MERGE (c)-[:IN_SECTION]->(s)
                    """, new { rows });
                return true;
            });
        }

        // The :NEXT chain runs within a strategy, in reading order.
        foreach (var group in commit.Chunks.GroupBy(c => c.Strategy))
        {
            var ordered = group.OrderBy(c => c.StrategyOrdinal).ToList();
            var pairs = ordered.Zip(ordered.Skip(1), (a, b) => new Dictionary<string, object?> { ["a"] = a.Id, ["b"] = b.Id }).ToList();
            foreach (var batch in pairs.Chunk(_batchSize))
            {
                var rows = batch.ToList();
                await session.ExecuteWriteAsync(async tx =>
                {
                    await tx.RunAsync("""
                        UNWIND $rows AS row
                        MATCH (a:Chunk {id: row.a}), (b:Chunk {id: row.b})
                        MERGE (a)-[:NEXT]->(b)
                        """, new { rows });
                    return true;
                });
            }
        }

        // Entities, grouped by type so the label can be interpolated safely.
        foreach (var typeGroup in commit.Entities.GroupBy(e => e.Type))
        {
            var label = SafeIdentifier(typeGroup.Key, _ontology.IsNodeType(typeGroup.Key));
            foreach (var batch in typeGroup.Chunk(_batchSize))
            {
                var rows = batch.Select(e => new Dictionary<string, object?>
                {
                    ["key"] = e.Key, ["name"] = e.Name, ["title"] = e.Name, ["docId"] = docId,
                    ["year"] = e.Year, ["aliases"] = e.Aliases, ["mentionCount"] = e.MentionCount
                }).ToList();

                await session.ExecuteWriteAsync(async tx =>
                {
                    await tx.RunAsync($$"""
                        UNWIND $rows AS row
                        MERGE (e:{{label}} {key: row.key})
                        SET e += row
                        """, new { rows });
                    return true;
                });
            }
        }

        // MENTIONS, evidence-grounded.
        var mentions = commit.Entities
            .SelectMany(e => e.ChunkIds.Select(c => new Dictionary<string, object?> { ["chunk"] = c, ["key"] = e.Key }))
            .ToList();
        foreach (var batch in mentions.Chunk(_batchSize))
        {
            var rows = batch.ToList();
            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync("""
                    UNWIND $rows AS row
                    MATCH (c:Chunk {id: row.chunk})
                    MATCH (e {key: row.key})
                    MERGE (c)-[:MENTIONS]->(e)
                    """, new { rows });
                return true;
            });
        }

        // Semantic relations, grouped by predicate.
        foreach (var predicateGroup in commit.Relations.GroupBy(r => r.Predicate))
        {
            var type = SafeIdentifier(predicateGroup.Key, _ontology.IsPredicate(predicateGroup.Key));
            foreach (var batch in predicateGroup.Chunk(_batchSize))
            {
                var rows = batch.Select(r => new Dictionary<string, object?>
                {
                    ["from"] = r.SubjectKey, ["to"] = r.ObjectKey, ["predicate"] = r.Predicate,
                    ["confidence"] = r.Confidence, ["mentionCount"] = r.MentionCount,
                    ["chunkIds"] = r.ChunkIds, ["evidence"] = r.Evidence, ["verdict"] = r.Verdict,
                    ["verdictReason"] = r.VerdictReason, ["flipped"] = r.Flipped, ["derived"] = false,
                    ["properties"] = string.Join(";", r.Properties.Select(p => $"{p.Key}={p.Value}"))
                }).ToList();

                await session.ExecuteWriteAsync(async tx =>
                {
                    await tx.RunAsync($$"""
                        UNWIND $rows AS row
                        MATCH (a {key: row.from}), (b {key: row.to})
                        MERGE (a)-[r:{{type}}]->(b)
                        SET r += row
                        """, new { rows });
                    return true;
                });
            }
        }
    }

    public async Task<int> ComputeDerivedEdgesAsync(string docId, CancellationToken ct = default)
    {
        await using var session = Session();
        return await session.ExecuteWriteAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (a:Person)-[:ACTED_IN|DIRECTED|WROTE|PRODUCED|COMPOSED_FOR]->(w)
                MATCH (b:Person)-[:ACTED_IN|DIRECTED|WROTE|PRODUCED|COMPOSED_FOR]->(w)
                WHERE a.key < b.key AND w.docId = $docId
                WITH a, b, count(DISTINCT w) AS shared, collect(DISTINCT coalesce(w.title, w.name))[..5] AS titles
                MERGE (a)-[c:COLLABORATED_WITH]->(b)
                  SET c.count = shared, c.titles = titles, c.derived = true,
                      c.confidence = 1.0, c.predicate = 'COLLABORATED_WITH', c.mentionCount = shared
                RETURN count(*) AS written
                """, new { docId });
            var record = await cursor.SingleAsync();
            return record["written"].As<int>();
        });
    }

    // ----- expand ---------------------------------------------------------

    public async Task<ExpandResult> ExpandAsync(string docId, IReadOnlyList<string> chunkIds, GraphHops hops,
        double minConfidence, bool includeDerived, CancellationToken ct = default)
    {
        if (chunkIds.Count == 0) return new ExpandResult();

        await using var session = Session();
        var records = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (c:Chunk) WHERE c.id IN $ids
                OPTIONAL MATCH (prev)-[:NEXT]->(c)
                OPTIONAL MATCH (c)-[:NEXT]->(next)
                OPTIONAL MATCH (c)-[:IN_SECTION]->(s:Section)
                OPTIONAL MATCH (c)-[:MENTIONS]->(e) WHERE $entityHop
                OPTIONAL MATCH (e)-[r]-(e2) WHERE $relHop AND r.confidence >= $minConf
                                              AND (coalesce(r.derived,false) = false OR $includeDerived)
                                              AND e2.key IS NOT NULL
                OPTIONAL MATCH (c2:Chunk)-[:MENTIONS]->(e2)
                RETURN c.id AS id, c.text AS text,
                       prev.text AS prevText, prev.id AS prevId,
                       next.text AS nextText, next.id AS nextId,
                       s.summary AS sectionSummary,
                       collect(DISTINCT {name: e.name, type: labels(e)[0], key: e.key,
                                         year: e.year, mentionCount: e.mentionCount}) AS entities,
                       collect(DISTINCT {pred: type(r), fromKey: startNode(r).key, toKey: endNode(r).key,
                                         fromName: coalesce(startNode(r).name, startNode(r).title),
                                         toName: coalesce(endNode(r).name, endNode(r).title),
                                         conf: r.confidence, mentions: r.mentionCount,
                                         derived: coalesce(r.derived,false), evidence: r.evidence,
                                         verdict: r.verdict, viaChunk: c2.id}) AS related
                """, new
            {
                ids = chunkIds.ToList(),
                minConf = minConfidence,
                includeDerived,
                entityHop = hops.Entity,
                relHop = hops.Entity && hops.EntityRel
            });
            return await cursor.ToListAsync();
        });

        var chunks = new List<ExpandedChunk>();
        var touched = new Dictionary<string, GraphEntity>(StringComparer.Ordinal);
        var traversed = new Dictionary<(string, string, string), GraphEdge>();

        foreach (var record in records)
        {
            var entities = new List<GraphEntity>();
            foreach (var raw in record["entities"].As<List<object>>())
            {
                if (raw is not IDictionary<string, object> map || map["key"] is null) continue;
                var entity = new GraphEntity
                {
                    Key = map["key"].As<string>(),
                    Name = map["name"].As<string>() ?? "",
                    Type = map["type"].As<string>() ?? "Unknown",
                    Year = map.TryGetValue("year", out var y) && y is not null ? y.As<int>() : null,
                    MentionCount = map.TryGetValue("mentionCount", out var m) && m is not null ? m.As<int>() : 0,
                };
                entities.Add(entity);
                touched[entity.Key] = entity;
            }

            var related = new List<GraphEdge>();
            var relatedChunks = new List<string>();
            foreach (var raw in record["related"].As<List<object>>())
            {
                if (raw is not IDictionary<string, object> map || map["pred"] is null) continue;
                var edge = new GraphEdge
                {
                    From = map["fromKey"].As<string>() ?? "",
                    To = map["toKey"].As<string>() ?? "",
                    FromName = map["fromName"].As<string>() ?? "",
                    ToName = map["toName"].As<string>() ?? "",
                    Predicate = map["pred"].As<string>() ?? "",
                    Confidence = map.TryGetValue("conf", out var c) && c is not null ? c.As<double>() : 0,
                    MentionCount = map.TryGetValue("mentions", out var mc) && mc is not null ? mc.As<int>() : 1,
                    Derived = map.TryGetValue("derived", out var d) && d is not null && d.As<bool>(),
                    Evidence = map.TryGetValue("evidence", out var ev) ? ev?.As<string>() : null,
                    Verdict = map.TryGetValue("verdict", out var vd) ? vd?.As<string>() : null,
                };
                related.Add(edge);
                traversed[(edge.From, edge.Predicate, edge.To)] = edge;
                if (map.TryGetValue("viaChunk", out var via) && via is not null)
                    relatedChunks.Add(via.As<string>());
            }

            chunks.Add(new ExpandedChunk
            {
                Id = record["id"].As<string>(),
                Text = record["text"].As<string>() ?? "",
                PrevText = hops.Next ? record["prevText"]?.As<string>() : null,
                PrevId = hops.Next ? record["prevId"]?.As<string>() : null,
                NextText = hops.Next ? record["nextText"]?.As<string>() : null,
                NextId = hops.Next ? record["nextId"]?.As<string>() : null,
                SectionSummary = hops.Parent ? record["sectionSummary"]?.As<string>() : null,
                Entities = entities,
                Related = related,
                RelatedChunkIds = [.. relatedChunks.Distinct(StringComparer.Ordinal).Take(6)],
            });
        }

        return new ExpandResult
        {
            Chunks = chunks,
            EntitiesTouched = [.. touched.Values],
            EdgesTraversed = [.. traversed.Values],
        };
    }

    // ----- shortest path --------------------------------------------------

    public async Task<PathResult?> ShortestPathAsync(string docId, string fromKey, string toKey, int maxHops,
        double minConfidence, CancellationToken ct = default)
    {
        await using var session = Session();
        var record = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync($$"""
                MATCH (a {key: $from}), (b {key: $to})
                MATCH path = shortestPath(
                  (a)-[:ACTED_IN|DIRECTED|WROTE|PRODUCED|COMPOSED_FOR|SHOT_BY|EDITED_BY*..{{Math.Clamp(maxHops, 1, 20)}}]-(b)
                )
                RETURN [n IN nodes(path) | {name: coalesce(n.name, n.title),
                                            type: labels(n)[0], key: n.key, year: n.year}] AS nodes,
                       [r IN relationships(path) | type(r)] AS rels,
                       length(path) AS hops
                """, new { from = fromKey, to = toKey });
            var list = await cursor.ToListAsync();
            return list.FirstOrDefault();
        });

        if (record is null) return null;

        var nodes = record["nodes"].As<List<object>>()
            .OfType<IDictionary<string, object>>()
            .Select(m => new PathNode
            {
                Key = m["key"].As<string>() ?? "",
                Name = m["name"].As<string>() ?? "",
                Type = m["type"].As<string>() ?? "Unknown",
                Year = m.TryGetValue("year", out var y) && y is not null ? y.As<int>() : null,
            })
            .ToList();
        var rels = record["rels"].As<List<object>>().Select(r => r.As<string>()).ToList();

        return new PathResult
        {
            Hops = record["hops"].As<int>(),
            Nodes = nodes,
            Rels = rels,
            Narrative = PathNarrative.Render(nodes, rels, _ontology),
        };
    }

    // ----- aggregation ----------------------------------------------------

    public async Task<AggregationResult> AggregateAsync(string docId, string presetId, int? year, double minConfidence, CancellationToken ct = default)
    {
        var cypher = AggregationCypher.For(presetId);
        await using var session = Session();
        var records = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(cypher, new { docId, year });
            return await cursor.ToListAsync();
        });

        var columns = records.Count > 0 ? records[0].Keys.ToList() : [];
        var rows = records.Select(r => new AggregationRow(
            r.Keys.ToDictionary(k => k, k => Simplify(r[k])))).ToList();

        return new AggregationResult
        {
            PresetId = presetId,
            Title = AggregationCypher.TitleFor(presetId),
            Cypher = cypher,
            Columns = columns,
            Rows = rows,
        };
    }

    private static object? Simplify(object? value) => value switch
    {
        null => null,
        IEnumerable<object> list => string.Join(" · ", list.Select(v => v?.ToString())),
        _ => value
    };

    // ----- inspection -----------------------------------------------------

    public async Task<GraphSnapshot> SnapshotAsync(string docId, double minConfidence, bool includeDerived, int limit, CancellationToken ct = default)
    {
        await using var session = Session();
        return await session.ExecuteReadAsync(async tx =>
        {
            var nodeCursor = await tx.RunAsync("""
                MATCH (e) WHERE e.docId = $docId AND e.key IS NOT NULL
                RETURN e.key AS key, labels(e)[0] AS type, coalesce(e.name, e.title) AS name,
                       e.year AS year, coalesce(e.mentionCount, 0) AS mentions
                LIMIT $limit
                """, new { docId, limit });
            var nodes = (await nodeCursor.ToListAsync()).Select(r => new GraphEntity
            {
                Key = r["key"].As<string>(),
                Type = r["type"].As<string>(),
                Name = r["name"].As<string>() ?? "",
                Year = r["year"]?.As<int>(),
                MentionCount = r["mentions"].As<int>(),
            }).ToList();

            var edgeCursor = await tx.RunAsync("""
                MATCH (a)-[r]->(b)
                WHERE a.docId = $docId AND a.key IS NOT NULL AND b.key IS NOT NULL
                  AND coalesce(r.confidence, 1.0) >= $minConf
                  AND (coalesce(r.derived, false) = false OR $includeDerived)
                RETURN a.key AS fromKey, b.key AS toKey,
                       coalesce(a.name, a.title) AS fromName, coalesce(b.name, b.title) AS toName,
                       type(r) AS predicate, coalesce(r.confidence, 1.0) AS confidence,
                       coalesce(r.mentionCount, 1) AS mentions, coalesce(r.derived, false) AS derived,
                       r.evidence AS evidence, r.verdict AS verdict, coalesce(r.flipped,false) AS flipped
                LIMIT $limit
                """, new { docId, minConf = minConfidence, includeDerived, limit });
            var edges = (await edgeCursor.ToListAsync()).Select(r => new GraphEdge
            {
                From = r["fromKey"].As<string>(),
                To = r["toKey"].As<string>(),
                FromName = r["fromName"].As<string>() ?? "",
                ToName = r["toName"].As<string>() ?? "",
                Predicate = r["predicate"].As<string>(),
                Confidence = r["confidence"].As<double>(),
                MentionCount = r["mentions"].As<int>(),
                Derived = r["derived"].As<bool>(),
                Evidence = r["evidence"]?.As<string>(),
                Verdict = r["verdict"]?.As<string>(),
                Flipped = r["flipped"].As<bool>(),
            }).ToList();

            return new GraphSnapshot
            {
                Nodes = nodes, Edges = edges,
                TotalNodes = nodes.Count, TotalEdges = edges.Count,
                Truncated = nodes.Count >= limit || edges.Count >= limit,
            };
        });
    }

    public async Task<IReadOnlyList<EntityRef>> SearchEntitiesAsync(string docId, string? type, string? query, int limit, CancellationToken ct = default)
    {
        await using var session = Session();
        var records = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (e) WHERE e.docId = $docId AND e.key IS NOT NULL
                  AND ($type IS NULL OR $type IN labels(e))
                  AND ($q IS NULL OR toLower(coalesce(e.name, e.title)) CONTAINS toLower($q))
                RETURN e.key AS key, coalesce(e.name, e.title) AS name, labels(e)[0] AS type,
                       coalesce(e.mentionCount, 0) AS mentions, e.year AS year
                ORDER BY mentions DESC, name ASC
                LIMIT $limit
                """, new { docId, type, q = query, limit });
            return await cursor.ToListAsync();
        });

        return [.. records.Select(r => new EntityRef
        {
            Key = r["key"].As<string>(),
            Name = r["name"].As<string>() ?? "",
            Type = r["type"].As<string>(),
            MentionCount = r["mentions"].As<int>(),
            Year = r["year"]?.As<int>(),
        })];
    }

    public async Task<GraphEdge?> GetEdgeAsync(string docId, string fromKey, string predicate, string toKey, CancellationToken ct = default)
    {
        var type = SafeIdentifier(predicate, _ontology.IsPredicate(predicate) || predicate == "COLLABORATED_WITH");
        await using var session = Session();
        var record = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync($$"""
                MATCH (a {key: $from})-[r:{{type}}]->(b {key: $to})
                RETURN coalesce(a.name, a.title) AS fromName, coalesce(b.name, b.title) AS toName,
                       coalesce(r.confidence, 1.0) AS confidence, coalesce(r.mentionCount, 1) AS mentions,
                       coalesce(r.derived, false) AS derived, r.evidence AS evidence,
                       r.verdict AS verdict, r.verdictReason AS verdictReason,
                       coalesce(r.chunkIds, []) AS chunkIds, coalesce(r.flipped, false) AS flipped
                """, new { from = fromKey, to = toKey });
            var list = await cursor.ToListAsync();
            return list.FirstOrDefault();
        });

        if (record is null) return null;
        return new GraphEdge
        {
            From = fromKey, To = toKey,
            FromName = record["fromName"].As<string>() ?? "",
            ToName = record["toName"].As<string>() ?? "",
            Predicate = predicate,
            Confidence = record["confidence"].As<double>(),
            MentionCount = record["mentions"].As<int>(),
            Derived = record["derived"].As<bool>(),
            Evidence = record["evidence"]?.As<string>(),
            Verdict = record["verdict"]?.As<string>(),
            VerdictReason = record["verdictReason"]?.As<string>(),
            ChunkIds = record["chunkIds"].As<List<object>>().Select(o => o.As<string>()).ToList(),
            Flipped = record["flipped"].As<bool>(),
        };
    }

    public async Task DeleteDocumentAsync(string docId, CancellationToken ct = default)
    {
        await using var session = Session();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("MATCH (n) WHERE n.docId = $docId DETACH DELETE n", new { docId });
            await tx.RunAsync("MATCH (d:Document {id: $docId}) DETACH DELETE d", new { docId });
            return true;
        });
    }

    public async Task<ProviderHealth> HealthAsync(CancellationToken ct = default)
    {
        try
        {
            await _driver.VerifyConnectivityAsync();
            await using var session = Session();
            var counts = await session.ExecuteReadAsync(async tx =>
            {
                var cursor = await tx.RunAsync("MATCH (n) RETURN count(n) AS nodes");
                var record = await cursor.SingleAsync();
                return record["nodes"].As<long>();
            });
            return new ProviderHealth("graph", ProviderHealth.Ok, $"Neo4j reachable, {counts} nodes.");
        }
        catch (Neo4jException ex)
        {
            return new ProviderHealth("graph", ProviderHealth.Unreachable, ex.Message);
        }
        catch (Exception ex) when (ex is ServiceUnavailableException or TimeoutException or IOException)
        {
            // AuraDB free instances pause after about a week of inactivity.
            return new ProviderHealth("graph", ProviderHealth.Paused,
                $"Neo4j did not respond ({ex.GetType().Name}). Free AuraDB instances pause when idle — resume it in the Aura console.");
        }
        catch (Exception ex)
        {
            return new ProviderHealth("graph", ProviderHealth.Unreachable, ex.Message);
        }
    }

    public async ValueTask DisposeAsync() => await _driver.DisposeAsync();

    /// <summary>
    /// Labels and relationship types cannot be parameterised in Cypher, so they are interpolated.
    /// Interpolation is only permitted for identifiers that came from the closed ontology and
    /// also match a strict identifier pattern.
    /// </summary>
    internal static string SafeIdentifier(string value, bool knownToOntology)
    {
        if (!knownToOntology)
            throw new ArgumentException($"'{value}' is not part of the ontology and will not be written to the graph.", nameof(value));
        if (!IdentifierPattern().IsMatch(value))
            throw new ArgumentException($"'{value}' is not a valid Cypher identifier.", nameof(value));
        return value;
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,63}$")]
    private static partial Regex IdentifierPattern();
}
