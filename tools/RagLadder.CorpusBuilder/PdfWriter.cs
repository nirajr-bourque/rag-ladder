using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace RagLadder.CorpusBuilder;

/// <summary>
/// Lays the flattened blocks out onto A4 pages. Heading font sizes are set well above the body
/// median so the parser's relative-font-size heading detection has a real signal to work with,
/// and every page carries a running header and footer so the running-line stripper does too.
/// </summary>
public sealed class CorpusPdfWriter
{
    private const double PageWidth = 595.28;
    private const double PageHeight = 841.89;
    private const double MarginLeft = 56;
    private const double MarginRight = 56;
    private const double MarginTop = 64;
    private const double MarginBottom = 56;

    private const double BodySize = 9.5;
    private const double H1Size = 18;
    private const double H2Size = 14.5;
    private const double H3Size = 12;
    private const double FrontMatterSize = 8.5;

    private readonly string _runningHeader;

    public CorpusPdfWriter(string runningHeader) => _runningHeader = runningHeader;

    public byte[] Build(IReadOnlyList<Block> blocks)
    {
        var builder = new PdfDocumentBuilder();
        var body = builder.AddStandard14Font(Standard14Font.Helvetica);
        var bold = builder.AddStandard14Font(Standard14Font.HelveticaBold);
        var mono = builder.AddStandard14Font(Standard14Font.Courier);

        PdfPageBuilder? page = null;
        double y = 0;
        var pageNumber = 0;

        void NewPage()
        {
            page = builder.AddPage(PageWidth, PageHeight);
            pageNumber++;
            y = PageHeight - MarginTop;

            page.AddText(MarkdownFlattener.Sanitize(_runningHeader), 7.5,
                new PdfPoint(MarginLeft, PageHeight - 34), body);
            page.AddText($"Page {pageNumber}", 7.5, new PdfPoint(PageWidth - MarginRight - 40, 30), body);
        }

        NewPage();

        foreach (var block in blocks)
        {
            if (block.Kind == BlockKind.PageBreak) { NewPage(); continue; }
            if (block.Kind == BlockKind.Blank) { y -= BodySize * 0.7; continue; }

            var (size, font, spacingBefore) = block.Kind switch
            {
                BlockKind.Heading1 => (H1Size, bold, 18.0),
                BlockKind.Heading2 => (H2Size, bold, 14.0),
                BlockKind.Heading3 => (H3Size, bold, 11.0),
                BlockKind.FrontMatter => (FrontMatterSize, mono, 1.0),
                BlockKind.TableRow => (BodySize, body, 1.0),
                _ => (BodySize, body, 2.0)
            };

            y -= spacingBefore;
            var lineHeight = size * 1.32;
            var maxWidth = PageWidth - MarginLeft - MarginRight;

            foreach (var line in WrapLine(page!, block.Text, size, font, maxWidth))
            {
                if (y - lineHeight < MarginBottom) NewPage();
                page!.AddText(line, size, new PdfPoint(MarginLeft, y), font);
                y -= lineHeight;
            }
        }

        return builder.Build();
    }

    private static IEnumerable<string> WrapLine(
        PdfPageBuilder page, string text, double size, PdfDocumentBuilder.AddedFont font, double maxWidth)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var current = "";

        foreach (var word in words)
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (Measure(page, candidate, size, font) <= maxWidth)
            {
                current = candidate;
                continue;
            }
            if (current.Length > 0) yield return current;
            current = word;

            // A single word longer than the line (a long invented name in a credit block).
            while (Measure(page, current, size, font) > maxWidth && current.Length > 4)
            {
                var cut = current.Length / 2;
                yield return current[..cut] + "-";
                current = current[cut..];
            }
        }
        if (current.Length > 0) yield return current;
    }

    private static double Measure(PdfPageBuilder page, string text, double size, PdfDocumentBuilder.AddedFont font)
    {
        var letters = page.MeasureText(text, size, new PdfPoint(0, 0), font);
        return letters.Count == 0 ? 0 : letters[^1].BoundingBox.Right;
    }
}
