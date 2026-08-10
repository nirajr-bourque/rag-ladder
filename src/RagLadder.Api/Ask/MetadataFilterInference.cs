using System.Text.RegularExpressions;
using RagLadder.Api.Models;

namespace RagLadder.Api.Ask;

/// <summary>
/// Stage 3 needs a filter, and typing one by hand during a demo is not a lesson about metadata
/// filtering. This derives one deterministically from the question against the document's own
/// front matter vocabulary — no model call, so the rung stays cheap and reproducible.
/// Traps 2 and 11 both turn on getting this right: the year narrows a title collision, and
/// docType plus a minimum year picks the superseding casting announcement.
/// </summary>
public static partial class MetadataFilterInference
{
    [GeneratedRegex(@"\b(1[89]\d{2}|20\d{2})\b")]
    private static partial Regex YearPattern();

    private static readonly (string Phrase, string DocType)[] DocTypeHints =
    [
        ("box office", "box-office"), ("opening weekend", "box-office"), ("gross", "box-office"),
        ("cast announcement", "casting"), ("casting", "casting"), ("recast", "casting-history"),
        ("award", "awards-record"), ("won", "awards-record"), ("nominat", "awards-record"),
        ("episode", "episode-guide"), ("season", "series-record"),
        ("press kit", "press-kit"), ("synopsis", "press-kit"),
        ("biography", "talent-record"), ("filmography", "talent-record"), ("credits", "talent-record"),
        ("festival", "festival"),
    ];

    public static ChunkFilter Infer(string question, IReadOnlyList<SectionRecord> sections)
    {
        var filter = new ChunkFilter();
        var lower = question.ToLowerInvariant();

        var years = YearPattern().Matches(question).Select(m => int.Parse(m.Value)).Distinct().OrderBy(y => y).ToArray();
        if (years.Length == 1) filter.Year = years[0];
        else if (years.Length > 1) filter.YearRange = [years[0], years[^1]];

        // Only apply a docType the document actually uses. Match loosely, because corpora name the
        // same idea differently — "box-office" here, "box-office-record" there.
        var available = sections.Select(s => s.FrontMatter.DocType).Where(d => d is not null).Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var (phrase, docType) in DocTypeHints)
        {
            if (!lower.Contains(phrase, StringComparison.Ordinal)) continue;
            var match = available.FirstOrDefault(d => string.Equals(d, docType, StringComparison.OrdinalIgnoreCase))
                        ?? available.FirstOrDefault(d => d.Contains(docType, StringComparison.OrdinalIgnoreCase))
                        ?? available.FirstOrDefault(d => docType.Contains(d, StringComparison.OrdinalIgnoreCase));
            if (match is null) continue;
            filter.DocType = match;
            break;
        }

        // Longest matching subject wins: "Fantastic Four: Rise of the Silver Surfer" must beat
        // "Fantastic Four" when both appear in the question.
        var subject = sections
            .Select(s => s.FrontMatter.Subject)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(s => question.Contains(s!, StringComparison.OrdinalIgnoreCase))
            .MaxBy(s => s!.Length);
        if (subject is not null) filter.Subject = subject;

        var studio = sections
            .Select(s => s.FrontMatter.Studio)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(s => question.Contains(s!, StringComparison.OrdinalIgnoreCase));
        if (studio is not null) filter.Studio = studio;

        // A subject filter plus a year filter can be over-tight when the subject's own section
        // carries a different year; prefer the subject and keep the year only if they agree.
        if (filter is { Subject: not null, Year: not null })
        {
            var subjectYears = sections
                .Where(s => string.Equals(s.FrontMatter.Subject, filter.Subject, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.FrontMatter.Year)
                .Where(y => y is not null)
                .ToHashSet();
            if (subjectYears.Count > 0 && !subjectYears.Contains(filter.Year)) filter.Year = null;
        }

        return filter;
    }
}
