using System.Text;
using RagLadder.Api.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace RagLadder.Api.Parsing;

public sealed class ScannedPdfException(string message) : Exception(message);

/// <summary>
/// PdfPig extraction (spec §5.1): per-page text, page numbers and word bounding boxes; font size
/// relative to the document median for heading detection; hyphenated line breaks rejoined;
/// running headers and footers stripped; scanned PDFs rejected outright.
/// </summary>
public sealed class PdfDocumentParser
{
    private const double LineGroupingTolerance = 2.5;
    private const int MinCharsPerPageForTextLayer = 50;
    private const double RunningLineThreshold = 0.60;

    public ParsedDocument Parse(Stream pdfStream)
    {
        using var document = PdfDocument.Open(pdfStream);
        var pageLines = new List<List<ParsedLine>>();

        foreach (var page in document.GetPages())
            pageLines.Add(ExtractLines(page));

        var charsPerPage = pageLines.Select(p => p.Sum(l => l.Text.Length)).OrderBy(x => x).ToArray();
        if (charsPerPage.Length == 0)
            throw new ScannedPdfException("The PDF contains no pages.");

        var median = charsPerPage[charsPerPage.Length / 2];
        if (median < MinCharsPerPageForTextLayer)
            throw new ScannedPdfException(
                $"This PDF has no usable text layer (median {median} characters per page). " +
                "Scanned documents are out of scope — OCR is not part of this demo.");

        var removed = RemoveRunningLines(pageLines);

        var text = new StringBuilder();
        var pageOffsets = new List<int>();
        var pages = new List<ParsedPage>();
        var allLines = new List<ParsedLine>();

        for (var i = 0; i < pageLines.Count; i++)
        {
            pageOffsets.Add(text.Length);
            var joined = JoinHyphenatedBreaks(pageLines[i]);
            var pageText = string.Join('\n', joined.Select(l => l.Text));
            pages.Add(new ParsedPage { Number = i + 1, Text = pageText, Lines = joined });
            allLines.AddRange(joined);
            text.Append(pageText);
            if (i < pageLines.Count - 1) text.Append("\n\n");
        }

        return new ParsedDocument
        {
            Text = text.ToString(),
            Pages = pages,
            Lines = allLines,
            PageStartOffsets = pageOffsets,
            RemovedRunningLines = removed,
        };
    }

    private static List<ParsedLine> ExtractLines(Page page)
    {
        var words = page.GetWords().ToList();
        if (words.Count == 0) return [];

        // Group words into lines by baseline, then order left to right.
        var groups = new List<List<Word>>();
        foreach (var word in words.OrderByDescending(w => w.BoundingBox.Bottom).ThenBy(w => w.BoundingBox.Left))
        {
            var target = groups.FirstOrDefault(g =>
                Math.Abs(g[0].BoundingBox.Bottom - word.BoundingBox.Bottom) <= LineGroupingTolerance);
            if (target is null) groups.Add([word]);
            else target.Add(word);
        }

        var lines = new List<ParsedLine>();
        foreach (var group in groups)
        {
            var ordered = group.OrderBy(w => w.BoundingBox.Left).ToList();
            var text = string.Join(' ', ordered.Select(w => w.Text)).Trim();
            if (text.Length == 0) continue;

            var letters = ordered.SelectMany(w => w.Letters).ToList();
            var fontSize = letters.Count > 0 ? Math.Round(letters.Average(l => l.PointSize), 2) : 0;
            var bold = letters.Count > 0 &&
                       letters.Count(l => l.FontDetails?.IsBold == true) > letters.Count / 2;
            var relativeY = page.Height > 0 ? ordered[0].BoundingBox.Bottom / page.Height : 0.5;

            lines.Add(new ParsedLine
            {
                Text = text, FontSize = fontSize, Page = page.Number, Bold = bold,
                RelativeY = Math.Clamp(relativeY, 0, 1),
            });
        }
        return lines;
    }

    /// <summary>
    /// Lines appearing on more than 60% of pages are furniture, not content — but only if they sit
    /// in the top or bottom margin. Frequency alone is not enough: this corpus repeats
    /// "studio: Sinharaja Studios" and "market: null" in the body of nearly every page, and
    /// stripping those would silently destroy the metadata that stage 3 depends on.
    /// </summary>
    private static List<string> RemoveRunningLines(List<List<ParsedLine>> pages)
    {
        if (pages.Count < 3) return [];

        static bool InMargin(ParsedLine line) => line.RelativeY is > 0.90 or < 0.10;

        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var page in pages)
            foreach (var key in page.Where(InMargin).Select(l => Normalize(l.Text)).Distinct(StringComparer.Ordinal))
                occurrences[key] = occurrences.GetValueOrDefault(key) + 1;

        var threshold = pages.Count * RunningLineThreshold;
        var running = occurrences.Where(kv => kv.Value > threshold).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        if (running.Count == 0) return [];

        var removedSamples = new List<string>();
        foreach (var page in pages)
        {
            var kept = page.Where(l => !(InMargin(l) && running.Contains(Normalize(l.Text)))).ToList();
            removedSamples.AddRange(page.Except(kept).Select(l => l.Text));
            page.Clear();
            page.AddRange(kept);
        }
        return [.. removedSamples.Distinct(StringComparer.Ordinal).Take(10)];
    }

    /// <summary>Page numbers vary, so normalise digits away before counting repeats.</summary>
    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text) sb.Append(char.IsDigit(c) ? '#' : c);
        return sb.ToString().Trim();
    }

    private static List<ParsedLine> JoinHyphenatedBreaks(List<ParsedLine> lines)
    {
        var result = new List<ParsedLine>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (i + 1 < lines.Count && line.Text.EndsWith('-') && line.Text.Length > 1 &&
                char.IsLetter(line.Text[^2]) && lines[i + 1].Text.Length > 0 && char.IsLower(lines[i + 1].Text[0]))
            {
                var next = lines[i + 1];
                var firstSpace = next.Text.IndexOf(' ');
                var head = firstSpace < 0 ? next.Text : next.Text[..firstSpace];
                var tail = firstSpace < 0 ? "" : next.Text[(firstSpace + 1)..];

                result.Add(line with { Text = line.Text[..^1] + head });
                lines[i + 1] = next with { Text = tail };
                if (tail.Length == 0) i++;
                continue;
            }
            result.Add(line);
        }
        return result;
    }
}
