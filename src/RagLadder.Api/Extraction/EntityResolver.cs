using RagLadder.Api.Configuration;
using RagLadder.Api.Embedding;
using RagLadder.Api.Models;

namespace RagLadder.Api.Extraction;

public sealed record ResolutionOutcome(
    IReadOnlyList<ProposedEntity> Entities,
    IReadOnlyDictionary<string, string> KeyRemap,
    IReadOnlyList<MergeCandidate> AmbiguousMerges,
    int SurfaceForms,
    int CrossTypeBlocks,
    int CrossTypeNameCollisions);

/// <summary>
/// Domain-specific entity resolution (spec §6.4). Generic cosine-similarity merging produces a
/// wrong graph in this domain, so the rules run in order and similarity is only consulted last:
///
///   1. Type barriers are absolute — Person / Character / Film / Studio never merge.
///   2. Films require year agreement; a remake keeps its own node.
///   3. Title normalisation (articles, subtitle punctuation, roman numerals).
///   4. Person names: diminutives, suffixes, initials — with disjoint role clusters flagged
///      for a human rather than merged.
///   5. Characters are scoped to their work.
///   6. Similarity, and only now: cosine >= 0.92 and Jaro-Winkler >= 0.88.
///   7. Studio suffixes stripped for comparison, full form retained for display.
/// </summary>
public sealed class EntityResolver(DomainOptions options, NameNormalizer normalizer, IEmbedder embedder)
{
    public async Task<ResolutionOutcome> ResolveAsync(
        IReadOnlyList<ProposedEntity> input,
        IReadOnlyDictionary<string, IReadOnlyList<string>> roleClustersByKey,
        CancellationToken ct = default)
    {
        var surfaceForms = input.Count;
        var crossTypeBlocks = 0;
        var ambiguous = new List<MergeCandidate>();

        // Rule 1 is structural: grouping by type makes a cross-type merge unrepresentable.
        var byType = input.GroupBy(e => e.Type, StringComparer.Ordinal).ToList();

        // Count the collisions the type barrier prevented, so the review UI can prove it did work.
        var namesByType = input
            .GroupBy(e => e.Type, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => normalizer.Normalize(g.Key, e.Name)).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        if (namesByType.TryGetValue(Ontology.PersonType, out var people) &&
            namesByType.TryGetValue(Ontology.CharacterType, out var characters))
            crossTypeBlocks = people.Intersect(characters, StringComparer.Ordinal).Count();

        // The same count across every type pair, not just Person/Character. In this corpus the
        // sharpest case is a series named after its own lead — "Loki" is a Character and a
        // TVSeries — which the Person/Character counter alone would never notice.
        var typesByName = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (type, names) in namesByType)
            foreach (var name in names)
            {
                if (!typesByName.TryGetValue(name, out var types)) typesByName[name] = types = [];
                types.Add(type);
            }
        var crossTypeNameCollisions = typesByName.Count(kv => kv.Value.Count > 1);

        var resolved = new List<ProposedEntity>();
        var remap = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var group in byType)
        {
            var type = group.Key;
            var candidates = group.OrderByDescending(e => e.MentionCount).ToList();

            // Embeddings only needed where similarity may actually be consulted.
            var vectors = await EmbedNamesAsync(type, candidates, ct);

            var clusters = new List<List<ProposedEntity>>();
            foreach (var candidate in candidates)
            {
                var target = clusters.FirstOrDefault(cluster =>
                    ShouldMerge(type, cluster[0], candidate, vectors, roleClustersByKey, ambiguous));
                if (target is null) clusters.Add([candidate]);
                else target.Add(candidate);
            }

            foreach (var cluster in clusters)
                resolved.Add(Collapse(cluster, remap));
        }

        return new ResolutionOutcome(resolved, remap, ambiguous, surfaceForms, crossTypeBlocks, crossTypeNameCollisions);
    }

    private async Task<Dictionary<string, float[]>> EmbedNamesAsync(
        string type, List<ProposedEntity> candidates, CancellationToken ct)
    {
        if (candidates.Count < 2) return [];
        var names = candidates.Select(c => $"{type}: {c.Name}").ToList();
        var vectors = await embedder.EmbedAsync(names, ct);
        var map = new Dictionary<string, float[]>(StringComparer.Ordinal);
        for (var i = 0; i < candidates.Count; i++) map[candidates[i].Key] = vectors[i];
        return map;
    }

    private bool ShouldMerge(
        string type,
        ProposedEntity left,
        ProposedEntity right,
        IReadOnlyDictionary<string, float[]> vectors,
        IReadOnlyDictionary<string, IReadOnlyList<string>> roleClusters,
        List<MergeCandidate> ambiguous)
    {
        if (options.BlockCrossTypeMerge && left.Type != right.Type) return false;

        var leftName = normalizer.Normalize(type, left.Name);
        var rightName = normalizer.Normalize(type, right.Name);

        switch (type)
        {
            // Rule 2: The Thaw (1998) and The Thaw (2024) stay separate and get linked by REMAKE_OF.
            case "Film":
                // The year is a barrier, not a tie-breaker, so it is checked before anything else.
                // Previously this guard sat *after* the exact-name test, so two films whose titles
                // merely resembled each other never reached it and fell through to fuzzy matching.
                // Sequels are the pathological case: "Spider-Man" and "Spider-Man 2" score 0.97
                // Jaro-Winkler with near-identical embeddings, so Spider-Man 2 (2004), Spider-Man 3
                // (2007) and The Amazing Spider-Man 2 (2014) were all absorbed into the 2002 film,
                // taking their cast and crew with them.
                if (left.Year is not null && right.Year is not null && left.Year != right.Year) return false;
                if (leftName == rightName) return true;
                // A differing sequel numeral is decisive even when a year is missing on one side —
                // fuzzy similarity cannot see the one character that carries the whole meaning.
                if (TrailingOrdinal(leftName) != TrailingOrdinal(rightName)) return false;
                return SimilarityMerge(left, right, leftName, rightName, vectors);

            // Rule 5: character identity is (name, work).
            case "Character":
                return leftName == rightName && left.WorkSlug == right.WorkSlug;

            case "Person":
            {
                var exact = leftName == rightName;
                var initials = !exact && normalizer.InitialsCompatible(left.Name, right.Name);
                if (!exact && !initials) return SimilarityMerge(left, right, leftName, rightName, vectors);

                // Rule 4's exception: same name, incompatible roles in disjoint years is exactly the
                // ambiguity the review gate exists for. Flag it; do not decide it here.
                if (HasDisjointRoleClusters(left, right, roleClusters))
                {
                    ambiguous.Add(new MergeCandidate
                    {
                        LeftKey = left.Key, RightKey = right.Key,
                        LeftName = left.Name, RightName = right.Name,
                        Type = type,
                        Reason = "Same name, but the two mentions hold incompatible role clusters in disjoint years.",
                        Similarity = 1.0,
                    });
                    return false;
                }
                return true;
            }

            default:
                return leftName == rightName || SimilarityMerge(left, right, leftName, rightName, vectors);
        }
    }

    /// <summary>
    /// The sequel numeral at the end of a title, as "" when there is none. Roman numerals are
    /// included because film series use both conventions, sometimes within one franchise.
    /// </summary>
    private static string TrailingOrdinal(string name)
    {
        var token = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrEmpty(token)) return "";
        if (token.All(char.IsAsciiDigit)) return token;
        return token.All(c => "ivxIVX".Contains(c)) ? token.ToLowerInvariant() : "";
    }

    /// <summary>Rule 6 — only reached once rules 1 to 5 have had their say.</summary>
    private bool SimilarityMerge(
        ProposedEntity left, ProposedEntity right,
        string leftName, string rightName,
        IReadOnlyDictionary<string, float[]> vectors)
    {
        var jaro = JaroWinkler.Similarity(leftName, rightName);
        if (jaro < options.EntityMergeJaroWinkler) return false;

        if (!vectors.TryGetValue(left.Key, out var a) || !vectors.TryGetValue(right.Key, out var b))
            return false;

        return VectorMath.Cosine(a, b) >= options.EntityMergeCosine;
    }

    private static bool HasDisjointRoleClusters(
        ProposedEntity left, ProposedEntity right,
        IReadOnlyDictionary<string, IReadOnlyList<string>> roleClusters)
    {
        if (!roleClusters.TryGetValue(left.Key, out var a) || !roleClusters.TryGetValue(right.Key, out var b))
            return false;
        if (a.Count == 0 || b.Count == 0) return false;
        return !a.Intersect(b, StringComparer.Ordinal).Any();
    }

    /// <summary>Canonical name is the most frequent surface form; every variant is kept in aliases.</summary>
    private static ProposedEntity Collapse(List<ProposedEntity> cluster, Dictionary<string, string> remap)
    {
        var canonical = cluster
            .GroupBy(e => e.Name, StringComparer.Ordinal)
            .OrderByDescending(g => g.Sum(e => e.MentionCount))
            .ThenByDescending(g => g.Count())
            .First().Key;

        var primary = cluster.First(e => e.Name == canonical);
        var merged = new ProposedEntity
        {
            Key = primary.Key,
            Name = canonical,
            Type = primary.Type,
            Year = cluster.Select(e => e.Year).FirstOrDefault(y => y is not null),
            WorkSlug = primary.WorkSlug,
            MentionCount = cluster.Sum(e => e.MentionCount),
            Evidence = primary.Evidence,
        };

        foreach (var e in cluster)
        {
            remap[e.Key] = merged.Key;
            foreach (var chunkId in e.ChunkIds) merged.ChunkIds.Add(chunkId);
            if (!string.Equals(e.Name, canonical, StringComparison.Ordinal) && !merged.Aliases.Contains(e.Name))
                merged.Aliases.Add(e.Name);
            foreach (var alias in e.Aliases)
                if (!merged.Aliases.Contains(alias) && !string.Equals(alias, canonical, StringComparison.Ordinal))
                    merged.Aliases.Add(alias);
        }

        return merged;
    }
}
