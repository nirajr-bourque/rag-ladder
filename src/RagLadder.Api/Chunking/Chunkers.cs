using RagLadder.Api.Configuration;
using RagLadder.Api.Models;

namespace RagLadder.Api.Chunking;

/// <summary>
/// Character-based token approximation. Deliberately not the model tokenizer: chunk boundaries
/// must be identical whether or not the ONNX vocab is present, otherwise the same document would
/// chunk differently on two machines and the traps would stop being reproducible.
/// </summary>
public static class TokenEstimator
{
    public const double CharsPerToken = 4.0;
    public static int Count(string text) => (int)Math.Ceiling(text.Length / CharsPerToken);
    public static int Chars(int tokens) => (int)(tokens * CharsPerToken);
}

public sealed record ChunkSpan(int Start, int End, string Text);

public interface IChunker
{
    string Strategy { get; }
    IReadOnlyList<ChunkSpan> Split(string text, int sectionStartChar);
}

/// <summary>
/// 400 tokens, zero overlap. Deliberately bad — this is the stage-1 baseline whose hard cuts
/// break trap 1, and the whole point of stage 2 is watching that failure disappear.
/// </summary>
public sealed class FixedChunker(ChunkingOptions options) : IChunker
{
    public string Strategy => ChunkStrategies.Fixed;

    public IReadOnlyList<ChunkSpan> Split(string text, int sectionStartChar)
    {
        var size = TokenEstimator.Chars(options.FixedTokens);
        var spans = new List<ChunkSpan>();
        for (var i = 0; i < text.Length; i += size)
        {
            var end = Math.Min(i + size, text.Length);
            var slice = text[i..end];
            if (slice.Trim().Length == 0) continue;
            spans.Add(new ChunkSpan(sectionStartChar + i, sectionStartChar + end, slice));
        }
        return spans;
    }
}

/// <summary>
/// 400 tokens with 80 tokens of overlap, splitting on paragraph, then line, then sentence, then
/// a hard cut as the last resort.
/// </summary>
public sealed class RecursiveChunker(ChunkingOptions options) : IChunker
{
    private static readonly string[] Separators = ["\n\n", "\n", ". "];

    public string Strategy => ChunkStrategies.Recursive;

    public IReadOnlyList<ChunkSpan> Split(string text, int sectionStartChar)
    {
        var target = TokenEstimator.Chars(options.RecursiveTokens);
        var overlap = TokenEstimator.Chars(options.RecursiveOverlapTokens);
        var pieces = SplitRecursive(text, 0, target);

        // Re-assemble the atoms into target-sized windows, carrying the overlap forward.
        var spans = new List<ChunkSpan>();
        var bufferStart = -1;
        var bufferEnd = -1;

        foreach (var piece in pieces)
        {
            if (bufferStart < 0) { bufferStart = piece.Start; bufferEnd = piece.End; continue; }

            if (piece.End - bufferStart <= target)
            {
                bufferEnd = piece.End;
                continue;
            }

            spans.Add(Make(text, bufferStart, bufferEnd, sectionStartChar));
            bufferStart = Math.Max(0, Math.Min(piece.Start, bufferEnd - overlap));
            bufferEnd = piece.End;
        }

        if (bufferStart >= 0 && bufferEnd > bufferStart)
            spans.Add(Make(text, bufferStart, bufferEnd, sectionStartChar));

        return [.. spans.Where(s => s.Text.Trim().Length > 0)];
    }

    private static ChunkSpan Make(string text, int start, int end, int offset) =>
        new(offset + start, offset + end, text[start..end]);

    private static List<(int Start, int End)> SplitRecursive(string text, int depth, int target)
    {
        if (text.Length <= target || depth >= Separators.Length)
            return HardCut(text, target);

        var separator = Separators[depth];
        var atoms = new List<(int Start, int End)>();
        var cursor = 0;

        while (cursor < text.Length)
        {
            var next = text.IndexOf(separator, cursor, StringComparison.Ordinal);
            var end = next < 0 ? text.Length : next + separator.Length;
            var piece = text[cursor..end];

            if (piece.Length > target)
            {
                foreach (var (s, e) in SplitRecursive(piece, depth + 1, target))
                    atoms.Add((cursor + s, cursor + e));
            }
            else if (piece.Trim().Length > 0)
            {
                atoms.Add((cursor, end));
            }

            cursor = end;
            if (next < 0) break;
        }

        return atoms.Count > 0 ? atoms : HardCut(text, target);
    }

    private static List<(int Start, int End)> HardCut(string text, int target)
    {
        var atoms = new List<(int, int)>();
        for (var i = 0; i < text.Length; i += target)
            atoms.Add((i, Math.Min(i + target, text.Length)));
        return atoms.Count > 0 ? atoms : [(0, text.Length)];
    }
}

/// <summary>
/// Recursive chunks with a domain-tuned prefix naming the film or series and its year. Naming the
/// work in the prefix is exactly what fixes trap 6, where an episode-guide chunk has no idea
/// which series it belongs to (spec §5.2).
/// </summary>
public static class ContextualPrefix
{
    public static string Build(FrontMatter fm, string? sectionSummary)
    {
        var subject = fm.Subject ?? "Unknown subject";
        var year = fm.Year?.ToString() ?? "year unknown";
        var docType = fm.DocType ?? "document";
        var head = $"{subject} ({year}, {docType})";
        return string.IsNullOrWhiteSpace(sectionSummary) ? head + " — " : $"{head} — {sectionSummary.Trim()} ";
    }
}
