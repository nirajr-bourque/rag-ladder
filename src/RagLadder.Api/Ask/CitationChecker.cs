using System.Text.RegularExpressions;
using RagLadder.Api.Extraction;
using RagLadder.Api.Models;
using RagLadder.Api.Vector;

namespace RagLadder.Api.Ask;

/// <summary>
/// Stage 8. Citations are only worth showing if they are checked, so each bracketed marker is
/// resolved back to its chunk and the surrounding sentence is tested for lexical support against
/// that chunk. Groundedness is the fraction of factual sentences that carry a supported citation.
/// </summary>
public static partial class CitationChecker
{
    [GeneratedRegex(@"\[(\d{1,2})\]")]
    private static partial Regex Marker();

    public sealed record Result(IReadOnlyList<Citation> Citations, double Groundedness, string? Warning);

    public static Result Check(string answer, IReadOnlyList<RetrievedChunk> context)
    {
        if (context.Count == 0 || string.IsNullOrWhiteSpace(answer))
            return new Result([], 0, null);

        var citations = new List<Citation>();
        var sentences = SplitSentences(answer);
        var factual = 0;
        var supported = 0;

        foreach (var sentence in sentences)
        {
            var markers = Marker().Matches(sentence)
                .Select(m => int.Parse(m.Groups[1].Value))
                .Where(i => i >= 1 && i <= context.Count)
                .Distinct()
                .ToList();

            if (!LooksFactual(sentence)) continue;
            factual++;
            if (markers.Count == 0) continue;

            var sentenceSupported = false;
            foreach (var index in markers)
            {
                var chunk = context[index - 1];
                var quote = BestSupportingSpan(sentence, chunk.Text);
                var verified = quote is not null;
                sentenceSupported |= verified;

                if (citations.Any(c => c.Index == index)) continue;
                citations.Add(new Citation
                {
                    Index = index,
                    ChunkId = chunk.ChunkId,
                    Page = chunk.Page,
                    Section = chunk.Section,
                    Quote = quote,
                    Verified = verified,
                });
            }
            if (sentenceSupported) supported++;
        }

        var groundedness = factual == 0 ? 1.0 : (double)supported / factual;
        string? warning = null;
        if (factual > 0 && citations.Count == 0)
            warning = "The answer carries no resolvable citation markers, so nothing in it could be verified against the retrieved text.";
        else if (citations.Any(c => !c.Verified))
            warning = $"{citations.Count(c => !c.Verified)} citation(s) point at a chunk that does not visibly support the claim.";

        return new Result([.. citations.OrderBy(c => c.Index)], Math.Round(groundedness, 3), warning);
    }

    /// <summary>
    /// Finds the span of the cited chunk that best overlaps the sentence's content words. A quote
    /// is returned only when the overlap is substantial, so a mis-citation stays visible.
    /// </summary>
    private static string? BestSupportingSpan(string sentence, string chunkText)
    {
        var sentenceTerms = Bm25.Tokenize(Marker().Replace(sentence, " ")).Distinct().ToHashSet(StringComparer.Ordinal);
        if (sentenceTerms.Count == 0) return null;

        string? best = null;
        double bestScore = 0;

        foreach (var candidate in SplitSentences(chunkText))
        {
            var terms = Bm25.Tokenize(candidate).Distinct().ToHashSet(StringComparer.Ordinal);
            if (terms.Count == 0) continue;
            var overlap = (double)sentenceTerms.Intersect(terms, StringComparer.Ordinal).Count() / sentenceTerms.Count;
            if (overlap > bestScore) { bestScore = overlap; best = candidate.Trim(); }
        }

        return bestScore >= 0.4 ? Trim(best!) : null;
    }

    private static string Trim(string text) => text.Length <= 220 ? text : text[..220] + "…";

    private static List<string> SplitSentences(string text)
    {
        var sentences = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('.' or '!' or '?' or '\n')) continue;
            // Do not split inside a decimal figure or an abbreviation such as "Vol. 2".
            if (text[i] == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])) continue;
            var slice = text[start..(i + 1)].Trim();
            if (slice.Length > 0) sentences.Add(slice);
            start = i + 1;
        }
        var tail = text[start..].Trim();
        if (tail.Length > 0) sentences.Add(tail);
        return sentences;
    }

    /// <summary>A sentence asserting something checkable, as opposed to framing or hedging.</summary>
    private static bool LooksFactual(string sentence)
    {
        var stripped = Marker().Replace(sentence, "").Trim();
        if (stripped.Length < 15) return false;
        return Bm25.Tokenize(stripped).Count >= 3;
    }
}
