using System.Text;
using System.Text.RegularExpressions;

namespace RagLadder.CorpusBuilder;

public enum BlockKind
{
    Heading1,
    Heading2,
    Heading3,
    Body,
    FrontMatter,
    TableRow,
    Blank,
    PageBreak
}

public sealed record Block(BlockKind Kind, string Text);

/// <summary>
/// Turns the corpus markdown into a flat block stream the PDF writer can lay out. Markdown
/// emphasis is stripped, tables become pipe-separated rows, and the YAML front matter blocks are
/// emitted as plain <c>key: value</c> lines so the parser can recover them from the PDF text.
/// </summary>
public static partial class MarkdownFlattener
{
    [GeneratedRegex(@"^\s*(docType|subject|year|studio|market)\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex FrontMatterKey();

    [GeneratedRegex(@"^\s*\|?[\s:|-]{6,}\|?\s*$")]
    private static partial Regex TableSeparator();

    [GeneratedRegex(@"<!--\s*pagebreak\s*-->", RegexOptions.IgnoreCase)]
    private static partial Regex PageBreakMarker();

    public static List<Block> Flatten(string markdown, IReadOnlyCollection<string> pageBreakAfterAnchors)
    {
        var blocks = new List<Block>();
        var inFence = false;

        foreach (var rawLine in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (PageBreakMarker().IsMatch(line))
            {
                blocks.Add(new Block(BlockKind.PageBreak, ""));
                continue;
            }

            if (line.Length == 0)
            {
                if (blocks.Count > 0 && blocks[^1].Kind != BlockKind.Blank) blocks.Add(new Block(BlockKind.Blank, ""));
                continue;
            }

            if (inFence || FrontMatterKey().IsMatch(line))
            {
                blocks.Add(new Block(BlockKind.FrontMatter, Sanitize(line.Trim())));
                continue;
            }

            if (line.StartsWith("### ", StringComparison.Ordinal))
            { blocks.Add(new Block(BlockKind.Heading3, Sanitize(Strip(line[4..])))); goto anchorCheck; }
            if (line.StartsWith("## ", StringComparison.Ordinal))
            { blocks.Add(new Block(BlockKind.Heading2, Sanitize(Strip(line[3..])))); goto anchorCheck; }
            if (line.StartsWith("# ", StringComparison.Ordinal))
            { blocks.Add(new Block(BlockKind.Heading1, Sanitize(Strip(line[2..])))); goto anchorCheck; }

            if (TableSeparator().IsMatch(line)) continue;

            if (line.TrimStart().StartsWith('|'))
            {
                var cells = line.Trim().Trim('|').Split('|').Select(c => Strip(c.Trim()));
                blocks.Add(new Block(BlockKind.TableRow, Sanitize(string.Join("   ", cells))));
                goto anchorCheck;
            }

            if (line.TrimStart().StartsWith("---", StringComparison.Ordinal) && line.Trim().All(c => c == '-'))
            {
                blocks.Add(new Block(BlockKind.Blank, ""));
                continue;
            }

            var body = line.TrimStart().StartsWith("> ", StringComparison.Ordinal) ? line.TrimStart()[2..] : line;
            blocks.Add(new Block(BlockKind.Body, Sanitize(Strip(body))));

            anchorCheck:
            if (pageBreakAfterAnchors.Count > 0 &&
                pageBreakAfterAnchors.Any(a => blocks[^1].Text.Contains(a, StringComparison.OrdinalIgnoreCase)))
            {
                blocks.Add(new Block(BlockKind.PageBreak, ""));
            }
        }

        return blocks;
    }

    /// <summary>Removes markdown emphasis and link syntax, leaving readable prose.</summary>
    private static string Strip(string text)
    {
        text = Regex.Replace(text, @"\*\*(.+?)\*\*", "$1");
        text = Regex.Replace(text, @"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)", "$1");
        text = Regex.Replace(text, @"`(.+?)`", "$1");
        text = Regex.Replace(text, @"\[(.+?)\]\((.+?)\)", "$1");
        return text;
    }

    /// <summary>
    /// The Standard 14 fonts are WinAnsi-encoded, so transliterate anything outside it. The
    /// corpus is full of em dashes and curly quotes, which would otherwise fail to render.
    /// </summary>
    public static string Sanitize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark)
                continue;
            sb.Append(c switch
            {
                '—' or '–' => "-",
                '‘' or '’' => "'",
                '“' or '”' => "\"",
                '…' => "...",
                '·' or '•' => "-",
                ' ' => " ",
                '−' => "-",
                '′' => "'",
                '×' => "x",
                '≤' => "<=",
                '≥' => ">=",
                '≈' => "~",
                _ => c <= 0x7E && c >= 0x20 ? c.ToString() : Fallback(c)
            });
        }
        return sb.ToString();
    }

    private static string Fallback(char c) =>
        char.IsWhiteSpace(c) ? " " : c < 0x100 && char.IsLetterOrDigit(c) ? c.ToString() : "";
}
