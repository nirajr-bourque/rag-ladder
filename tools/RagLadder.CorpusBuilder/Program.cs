using System.Text.Json;
using RagLadder.CorpusBuilder;

// Renders the corpus markdown into the demo PDF.
//
// Two things happen here that matter to the demo rather than to the rendering:
//
//   * Both appendices are stripped, from "# APPENDIX A" onward. The corpus only asks for
//     Appendix B (the answer key mapping every invented name to its real-world counterpart) to be
//     removed, but Appendix A is worse: it is the trap map, and it spells out the connection path
//     — "Sunil Gunatilleke -> Fantastic Four (2005) <- Kasun Jayawardena -> Civil War <- Thevan
//     Rasiah" — in plain text. Ingesting that hands the retriever the answer to the very question
//     the stage-10 traversal is supposed to earn. Pass --strip-from "# APPENDIX B" to keep it.
//
//   * Page breaks are forced after the anchors listed in pagebreaks.json. Trap 1 requires a
//     filmography to split across a page boundary; leaving that to the vagaries of text flow
//     would make the trap non-reproducible.

var input = Arg(args, "--input") ?? "marvel-corpus-srilanka-full.md";
var output = Arg(args, "--output") ?? Path.Combine("corpus", "demo", "serendib-dossier.pdf");
var anchorsPath = Arg(args, "--pagebreaks") ?? Path.Combine("corpus", "demo", "pagebreaks.json");
var stripFrom = Arg(args, "--strip-from") ?? "# APPENDIX A";

if (!File.Exists(input))
{
    Console.Error.WriteLine($"Corpus markdown not found: {input}");
    return 1;
}

var markdown = File.ReadAllText(input);
var strippedChars = 0;
var cut = markdown.IndexOf(stripFrom, StringComparison.OrdinalIgnoreCase);
if (cut >= 0)
{
    strippedChars = markdown.Length - cut;
    markdown = markdown[..cut];
}
else
{
    Console.Error.WriteLine($"WARNING: '{stripFrom}' not found — nothing was stripped. Verify the build reference is not being ingested.");
}

string[] anchors = [];
if (File.Exists(anchorsPath))
{
    var config = JsonSerializer.Deserialize<PageBreakConfig>(File.ReadAllText(anchorsPath),
        new JsonSerializerOptions(JsonSerializerDefaults.Web));
    anchors = config?.BreakAfterLineContaining?.ToArray() ?? [];
}

var title = markdown.Split('\n').FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal))?[2..].Trim()
            ?? "Demo Corpus";

var blocks = MarkdownFlattener.Flatten(markdown, anchors);
var writer = new CorpusPdfWriter(title);
var bytes = writer.Build(blocks);

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
File.WriteAllBytes(output, bytes);

Console.WriteLine($"Wrote {output}");
Console.WriteLine($"  source          : {input}");
Console.WriteLine($"  stripped        : {strippedChars:N0} chars from '{stripFrom}' onward");
Console.WriteLine($"  blocks          : {blocks.Count:N0}");
Console.WriteLine($"  forced breaks   : {blocks.Count(b => b.Kind == BlockKind.PageBreak)} (anchors: {anchors.Length})");
Console.WriteLine($"  size            : {bytes.Length / 1024.0:N1} KiB");
return 0;

static string? Arg(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

internal sealed class PageBreakConfig
{
    public List<string>? BreakAfterLineContaining { get; set; }
}
