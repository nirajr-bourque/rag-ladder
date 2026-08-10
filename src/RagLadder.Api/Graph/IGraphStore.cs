using RagLadder.Api.Llm;
using RagLadder.Api.Models;

namespace RagLadder.Api.Graph;

public interface IGraphStore
{
    string Kind { get; }
    Task EnsureSchemaAsync(CancellationToken ct = default);
    Task CommitAsync(GraphCommit commit, CancellationToken ct = default);
    /// <summary>Computes COLLABORATED_WITH after commit. Returns the number of derived edges written.</summary>
    Task<int> ComputeDerivedEdgesAsync(string docId, CancellationToken ct = default);
    Task<ExpandResult> ExpandAsync(string docId, IReadOnlyList<string> chunkIds, GraphHops hops, double minConfidence, bool includeDerived, CancellationToken ct = default);
    Task<PathResult?> ShortestPathAsync(string docId, string fromKey, string toKey, int maxHops, double minConfidence, CancellationToken ct = default);
    Task<AggregationResult> AggregateAsync(string docId, string presetId, int? year, double minConfidence, CancellationToken ct = default);
    Task<GraphSnapshot> SnapshotAsync(string docId, double minConfidence, bool includeDerived, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<EntityRef>> SearchEntitiesAsync(string docId, string? type, string? query, int limit, CancellationToken ct = default);
    Task<GraphEdge?> GetEdgeAsync(string docId, string fromKey, string predicate, string toKey, CancellationToken ct = default);
    Task DeleteDocumentAsync(string docId, CancellationToken ct = default);
    Task<ProviderHealth> HealthAsync(CancellationToken ct = default);
}

/// <summary>
/// The aggregation presets from spec §7.3, one click each. The Cypher is surfaced in the UI
/// verbatim even when the local provider computes the answer, because seeing the query is the
/// point: these questions are unanswerable from top-k and trivial in Cypher.
/// </summary>
public static class AggregationCypher
{
    public const string StudioFilmCount = """
        MATCH (f:Film)-[:PRODUCED_BY]->(s:Studio)
        WHERE f.docId = $docId AND ($year IS NULL OR f.year = $year)
        RETURN s.name AS studio, count(f) AS films, collect(f.title)[..8] AS titles
        ORDER BY films DESC
        """;

    public const string DirectorCinematographerPairs = """
        MATCH (d:Person)-[:DIRECTED]->(f:Film)-[:SHOT_BY]->(c:Person)
        WHERE f.docId = $docId
        RETURN d.name AS director, c.name AS cinematographer,
               count(f) AS films, collect(f.title) AS titles
        ORDER BY films DESC
        """;

    public const string MultiFranchiseActors = """
        MATCH (p:Person)-[:ACTED_IN]->(f:Film)-[:PART_OF_FRANCHISE]->(fr:Franchise)
        WHERE f.docId = $docId
        WITH p, count(DISTINCT fr) AS franchises, collect(DISTINCT fr.name) AS names
        WHERE franchises > 1
        RETURN p.name AS person, franchises, names
        ORDER BY franchises DESC
        """;

    public const string AwardTallyByStudio = """
        MATCH (f:Film)-[:PRODUCED_BY]->(s:Studio)
        MATCH (f)-[w:WON]->(:AwardCategory)
        WHERE f.docId = $docId
        RETURN s.name AS studio, count(w) AS wins ORDER BY wins DESC
        """;

    public static string For(string presetId) => presetId switch
    {
        AggregationPresets.StudioFilmCount => StudioFilmCount,
        AggregationPresets.DirectorCinematographerPairs => DirectorCinematographerPairs,
        AggregationPresets.MultiFranchiseActors => MultiFranchiseActors,
        AggregationPresets.AwardTallyByStudio => AwardTallyByStudio,
        _ => throw new ArgumentOutOfRangeException(nameof(presetId), presetId, "Unknown aggregation preset.")
    };

    public static string TitleFor(string presetId) =>
        AggregationPresets.All.FirstOrDefault(p => p.Id == presetId).Title ?? presetId;

    public const string ShortestPath = """
        MATCH (a {key: $from, docId: $docId}), (b {key: $to, docId: $docId})
        MATCH path = shortestPath(
          (a)-[:ACTED_IN|DIRECTED|WROTE|PRODUCED|COMPOSED_FOR|SHOT_BY|EDITED_BY*..12]-(b)
        )
        RETURN [n IN nodes(path) | {name: coalesce(n.name, n.title),
                                    type: labels(n)[0], key: n.key, year: n.year}] AS nodes,
               [r IN relationships(path) | type(r)] AS rels,
               length(path) AS hops
        """;

    public const string Expand = """
        MATCH (c:Chunk) WHERE c.id IN $ids
        OPTIONAL MATCH (prev)-[:NEXT]->(c)
        OPTIONAL MATCH (c)-[:NEXT]->(next)
        OPTIONAL MATCH (c)-[:MENTIONS]->(e)
        OPTIONAL MATCH (e)-[r]->(e2) WHERE r.confidence >= $minConf
        OPTIONAL MATCH (c2:Chunk)-[:MENTIONS]->(e2)
        RETURN c.id AS id, c.text AS text,
               prev.text AS prevText, next.text AS nextText,
               collect(DISTINCT {name: e.name, type: labels(e)[0], key: e.key}) AS entities,
               collect(DISTINCT {pred: type(r), target: e2.name, targetKey: e2.key,
                                 conf: r.confidence, viaChunk: c2.id}) AS related
        """;
}

/// <summary>
/// Renders a traversal as prose. The answer to a path question is constructed from the graph,
/// not generated from retrieved text — the model's only job is to phrase what Cypher computed.
/// </summary>
public static class PathNarrative
{
    public static string Render(IReadOnlyList<PathNode> nodes, IReadOnlyList<string> rels, Ontology ontology)
    {
        if (nodes.Count < 2 || rels.Count != nodes.Count - 1) return "";

        var parts = new List<string>();
        for (var i = 0; i < rels.Count; i++)
        {
            var from = nodes[i];
            var to = nodes[i + 1];
            var forward = IsForward(rels[i], from.Type, to.Type, ontology);
            parts.Add(Phrase(rels[i], from, to, forward, i == 0));
        }
        return string.Join(", ", parts) + ".";
    }

    private static bool IsForward(string predicate, string fromType, string toType, Ontology ontology) =>
        ontology.CheckDirection(predicate, fromType, toType) != DirectionVerdict.Inverted;

    private static readonly string[] WorkTypes = ["Film", "TVSeries", "Episode", "Season"];

    private static string Phrase(string predicate, PathNode from, PathNode to, bool forward, bool first)
    {
        // After the first hop the clause continues from the previous node, so it opens with a
        // relative pronoun chosen by what that node is — "which" for a work, "who" for a person.
        var subject = first ? Label(from) : WorkTypes.Contains(from.Type) ? "which" : "who";
        var target = Label(to);

        // Bare verb phrases: the pronoun is supplied above, so it must not appear here too.
        var (verb, flip) = predicate switch
        {
            "ACTED_IN" => ("acted in", "starred"),
            "DIRECTED" => ("directed", "was directed by"),
            "WROTE" => ("wrote", "was written by"),
            "PRODUCED" => ("produced", "was produced by"),
            "COMPOSED_FOR" => ("composed for", "was scored by"),
            "SHOT_BY" => ("was shot by", "shot"),
            "EDITED_BY" => ("was edited by", "edited"),
            "PLAYED" => ("played", "was played by"),
            _ => (predicate.Replace('_', ' ').ToLowerInvariant(), "is linked to")
        };

        return forward ? $"{subject} {verb} {target}" : $"{subject} {flip} {target}";
    }

    private static string Label(PathNode n) =>
        n.Year is { } y && n.Type == "Film" ? $"{n.Name} ({y})" : n.Name;
}
