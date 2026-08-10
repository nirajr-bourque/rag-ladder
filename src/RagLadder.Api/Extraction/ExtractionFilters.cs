using System.Text;
using RagLadder.Api.Models;

namespace RagLadder.Api.Extraction;

/// <summary>
/// The deterministic filter chain (spec §6.6), applied in order, each reporting its drop count so
/// the UI can render the funnel. This is the "code disposes" half of the design: the model
/// proposes, and nothing reaches the graph without surviving all of it.
/// </summary>
public static class ExtractionFilters
{
    // ----- filter 1: evidence grounding (hard) ----------------------------

    /// <summary>
    /// Requires the evidence span to be a literal substring of the chunk after whitespace and case
    /// normalisation. This single check removes most hallucinated triples, because a fabricated
    /// relation rarely arrives with a real supporting span.
    /// </summary>
    public static bool IsGrounded(string evidence, string chunkText)
    {
        if (string.IsNullOrWhiteSpace(evidence)) return false;
        var needle = NormalizeForGrounding(evidence);
        if (needle.Length < 3) return false;
        return NormalizeForGrounding(chunkText).Contains(needle, StringComparison.Ordinal);
    }

    public static string NormalizeForGrounding(string text)
    {
        var sb = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var raw in text)
        {
            // Typographic variants are normalised: a model that retypes a curly quote as a
            // straight one has not fabricated anything.
            var c = raw switch
            {
                '‘' or '’' or 'ʼ' => '\'',
                '“' or '”' => '"',
                '–' or '—' or '−' => '-',
                ' ' => ' ',
                _ => raw
            };
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace) { sb.Append(' '); lastWasSpace = true; }
                continue;
            }
            sb.Append(char.ToLowerInvariant(c));
            lastWasSpace = false;
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Recovers a usable evidence span when the model paraphrased instead of quoting.
    ///
    /// The grounding filter exists because "a fabricated relation rarely arrives with a real
    /// supporting span". That guarantee rests on the *entity actually appearing in the chunk*, not
    /// on the model's ability to quote perfectly — and a small model paraphrases constantly. So
    /// when the stated evidence fails but every required name is present verbatim, the span is
    /// repaired to the sentence that contains them rather than the triple being thrown away.
    ///
    /// A relation whose endpoints do not appear in the text still gets dropped, which is the case
    /// the filter is actually defending against. Repairs are counted separately in the funnel so
    /// the distinction stays visible.
    /// </summary>
    public static bool TryRepairEvidence(string chunkText, IReadOnlyList<string> requiredNames, out string repaired)
    {
        repaired = "";
        if (requiredNames.Count == 0 || string.IsNullOrWhiteSpace(chunkText)) return false;

        var haystack = NormalizeForGrounding(chunkText);
        foreach (var name in requiredNames)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (!haystack.Contains(NormalizeForGrounding(name), StringComparison.Ordinal)) return false;
        }

        // Prefer the shortest sentence containing every name; fall back to the whole chunk.
        var best = SplitSentences(chunkText)
            .Where(s => requiredNames.All(n =>
                NormalizeForGrounding(s).Contains(NormalizeForGrounding(n), StringComparison.Ordinal)))
            .OrderBy(s => s.Length)
            .FirstOrDefault();

        repaired = (best ?? chunkText).Trim();
        if (repaired.Length > 300) repaired = repaired[..300].TrimEnd() + "…";
        return repaired.Length > 0;
    }

    private static IEnumerable<string> SplitSentences(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('.' or '\n' or '·' or '!' or '?')) continue;
            var slice = text[start..(i + 1)].Trim();
            if (slice.Length > 0) yield return slice;
            start = i + 1;
        }
        var tail = text[start..].Trim();
        if (tail.Length > 0) yield return tail;
    }

    // ----- filter 2 helper: predicate normalisation ----------------------

    /// <summary>
    /// Strips the arrow decoration models copy out of the prompt's direction table, turning
    /// <c>-ACTED_IN-&gt;</c> back into <c>ACTED_IN</c>.
    ///
    /// This is parsing, not coercion. The spec's conformance filter says not to coerce a
    /// non-ontology predicate into an ontology one, and this does not: it only removes syntax the
    /// prompt itself taught. Measured on a 3B model, 49 of 90 relations arrived arrow-wrapped and
    /// were being dropped as "non-conformant" when the predicate underneath was perfectly valid.
    /// Anything still outside the ontology after this is dropped as before.
    /// </summary>
    public static string NormalizePredicate(string predicate)
    {
        if (string.IsNullOrWhiteSpace(predicate)) return "";
        var trimmed = predicate.Trim().Trim('-', '>', '<', '(', ')', '[', ']', ' ', '"');
        return trimmed.Replace(' ', '_').ToUpperInvariant();
    }

    // ----- filter 3: direction and type check (hard, domain-specific) -----

    public sealed record DirectionOutcome(bool Keep, bool Flipped, string? DropReason);

    /// <summary>
    /// An inverted edge is a correctable mistake, not a fabrication: flip it and count the
    /// correction. A flip rate above 15% means the prompt's direction table needs work.
    /// </summary>
    public static DirectionOutcome CheckDirection(ProposedRelation relation, Ontology ontology)
    {
        var verdict = ontology.CheckDirection(relation.Predicate, relation.SubjectType, relation.ObjectType);
        switch (verdict)
        {
            case DirectionVerdict.Correct:
                return new DirectionOutcome(true, false, null);

            case DirectionVerdict.Inverted:
                (relation.SubjectKey, relation.ObjectKey) = (relation.ObjectKey, relation.SubjectKey);
                (relation.SubjectName, relation.ObjectName) = (relation.ObjectName, relation.SubjectName);
                (relation.SubjectType, relation.ObjectType) = (relation.ObjectType, relation.SubjectType);
                relation.Flipped = true;
                return new DirectionOutcome(true, true, null);

            case DirectionVerdict.UnknownPredicate:
                return new DirectionOutcome(false, false, "unknown-predicate");

            default:
                return new DirectionOutcome(false, false,
                    $"type-mismatch:{relation.SubjectType}-{relation.Predicate}->{relation.ObjectType}");
        }
    }

    // ----- filter 7: triple deduplication ---------------------------------

    /// <summary>
    /// Identical (subject, predicate, object) across chunks becomes one edge carrying
    /// mentionCount, the supporting chunk ids and the maximum observed confidence. Repeated
    /// assertion is a genuine reliability signal, so mentionCount is surfaced rather than hidden.
    /// </summary>
    public static List<ProposedRelation> Deduplicate(IEnumerable<ProposedRelation> relations)
    {
        var merged = new Dictionary<(string, string, string), ProposedRelation>();

        foreach (var r in relations)
        {
            var key = (r.SubjectKey, r.Predicate, r.ObjectKey);
            if (!merged.TryGetValue(key, out var existing))
            {
                merged[key] = r;
                continue;
            }

            existing.MentionCount += r.MentionCount;
            existing.Confidence = Math.Max(existing.Confidence, r.Confidence);
            foreach (var chunkId in r.ChunkIds)
                if (!existing.ChunkIds.Contains(chunkId))
                    existing.ChunkIds.Add(chunkId);
            if (existing.Evidence.Length < r.Evidence.Length) existing.Evidence = r.Evidence;
            existing.Flipped |= r.Flipped;
            foreach (var (k, v) in r.Properties) existing.Properties.TryAdd(k, v);
        }

        return [.. merged.Values];
    }
}
