using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RagLadder.Api.Models;

namespace RagLadder.Api.Parsing;

/// <summary>
/// Parses the structured header block that precedes each section (spec §3.3). The block survives
/// the PDF as plain <c>key: value</c> lines, so it is matched by key rather than by YAML syntax.
/// </summary>
public static partial class FrontMatterParser
{
    [GeneratedRegex(@"^\s*(docType|subject|year|studio|market)\s*:\s*(.*?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex KeyValue();

    public static bool IsFrontMatterLine(string line) => KeyValue().IsMatch(line);

    /// <summary>Applies one <c>key: value</c> line onto an accumulating front matter record.</summary>
    public static FrontMatter Apply(FrontMatter current, string line)
    {
        var match = KeyValue().Match(line);
        if (!match.Success) return current;

        var key = match.Groups[1].Value.ToLowerInvariant();
        var value = match.Groups[2].Value.Trim();
        if (value.Length == 0 || value.Equals("null", StringComparison.OrdinalIgnoreCase)) value = "";

        return key switch
        {
            "doctype" => current with { DocType = Nullify(value) },
            "subject" => current with { Subject = Nullify(value) },
            "studio" => current with { Studio = Nullify(value) },
            "market" => current with { Market = Nullify(value) },
            "year" => current with { Year = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ? y : current.Year },
            _ => current
        };
    }

    public static FrontMatter Parse(IEnumerable<string> lines)
    {
        var fm = FrontMatter.Empty;
        foreach (var line in lines) fm = Apply(fm, line);
        return fm;
    }

    private static string? Nullify(string value) => value.Length == 0 ? null : value;
}

/// <summary>
/// Splits a parsed document into sections. Headings are detected from font size relative to the
/// document median, with a textual fallback so that documents with a flat font profile still
/// segment sensibly.
/// </summary>
public sealed partial class SectionSegmenter
{
    private const double HeadingFontRatio = 1.12;

    [GeneratedRegex(@"^(PART\s+[IVXLC]+\b|Section\s+\d+\b|APPENDIX\s+[A-Z]\b)", RegexOptions.IgnoreCase)]
    private static partial Regex HeadingText();

    public IReadOnlyList<SectionRecord> Segment(string docId, ParsedDocument document)
    {
        var lines = document.Lines;
        if (lines.Count == 0) return [];

        var median = MedianFontSize(lines);
        var sections = new List<SectionRecord>();

        FrontMatter? pendingFrontMatter = null;
        var activeFrontMatter = FrontMatter.Empty;
        var currentHeading = "Preamble";
        var currentPage = 1;
        var body = new StringBuilder();
        var startChar = 0;
        var cursor = 0;
        var ordinal = 0;

        void Close(int endChar)
        {
            var text = body.ToString().Trim();
            if (text.Length == 0) return;
            sections.Add(new SectionRecord
            {
                Id = $"{docId}#s{ordinal}",
                DocId = docId,
                Ordinal = ordinal,
                Heading = currentHeading,
                StartChar = startChar,
                EndChar = endChar,
                Page = currentPage,
                FrontMatter = activeFrontMatter,
                Text = text,
            });
            ordinal++;
            body.Clear();
        }

        foreach (var line in lines)
        {
            var lineStart = document.Text.IndexOf(line.Text, cursor, StringComparison.Ordinal);
            if (lineStart < 0) lineStart = cursor;
            var lineEnd = lineStart + line.Text.Length;
            cursor = lineEnd;

            if (FrontMatterParser.IsFrontMatterLine(line.Text))
            {
                // A front matter block introduces the section that follows it.
                pendingFrontMatter = FrontMatterParser.Apply(pendingFrontMatter ?? FrontMatter.Empty, line.Text);
                continue;
            }

            if (IsHeading(line, median))
            {
                Close(lineStart);
                // A section without its own block inherits the last one seen.
                if (pendingFrontMatter is not null)
                {
                    activeFrontMatter = pendingFrontMatter;
                    pendingFrontMatter = null;
                }
                currentHeading = line.Text.Trim();
                currentPage = line.Page;
                startChar = lineStart;
                body.AppendLine(line.Text);
                continue;
            }

            if (body.Length == 0) startChar = lineStart;
            body.AppendLine(line.Text);
        }

        Close(document.Text.Length);
        return sections;
    }

    private static bool IsHeading(ParsedLine line, double median)
    {
        if (line.Text.Length > 120) return false;
        if (line.FontSize >= median * HeadingFontRatio) return true;
        if (line.Bold && line.FontSize > median) return true;
        return HeadingText().IsMatch(line.Text);
    }

    private static double MedianFontSize(IReadOnlyList<ParsedLine> lines)
    {
        var sizes = lines.Select(l => l.FontSize).Where(s => s > 0).OrderBy(s => s).ToArray();
        return sizes.Length == 0 ? 10 : sizes[sizes.Length / 2];
    }
}
