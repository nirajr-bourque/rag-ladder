using System.Globalization;
using System.Text;

namespace RagLadder.Api.Embedding;

/// <summary>
/// BERT WordPiece tokenizer for the uncased MiniLM checkpoints. Implemented here rather than
/// taken from a package so the ONNX input tensors (ids / mask / type ids) stay under our control.
/// </summary>
public sealed class WordPieceTokenizer
{
    private readonly Dictionary<string, int> _vocab;
    private const string Unk = "[UNK]";
    private const string Cls = "[CLS]";
    private const string Sep = "[SEP]";
    private const string Pad = "[PAD]";
    private const int MaxWordChars = 100;

    public int ClsId { get; }
    public int SepId { get; }
    public int PadId { get; }
    public int UnkId { get; }

    public WordPieceTokenizer(IEnumerable<string> vocabLines)
    {
        _vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        var i = 0;
        foreach (var line in vocabLines)
        {
            var token = line.TrimEnd('\r', '\n');
            _vocab.TryAdd(token, i);
            i++;
        }
        ClsId = _vocab.GetValueOrDefault(Cls, 101);
        SepId = _vocab.GetValueOrDefault(Sep, 102);
        PadId = _vocab.GetValueOrDefault(Pad, 0);
        UnkId = _vocab.GetValueOrDefault(Unk, 100);
    }

    public static WordPieceTokenizer FromFile(string path) => new(File.ReadLines(path));

    /// <summary>Single-sequence encoding: [CLS] a [SEP]. Truncates beyond the model's limit.</summary>
    public Encoded Encode(string text, int maxTokens)
    {
        var pieces = WordPieces(text);
        var budget = Math.Max(2, maxTokens) - 2;
        if (pieces.Count > budget) pieces = pieces.GetRange(0, budget);

        var ids = new List<long>(pieces.Count + 2) { ClsId };
        ids.AddRange(pieces);
        ids.Add(SepId);
        return new Encoded([.. ids], new long[ids.Count]);
    }

    /// <summary>
    /// Splits a long text into overlapping windows that each fit the model's context.
    ///
    /// This matters more than it looks. Chunks are 400 tokens by design, but all-MiniLM-L6-v2
    /// accepts 256 — so plain truncation makes the last third of every chunk invisible to
    /// retrieval, and a credit at the end of a long crew block simply cannot be found. Averaging
    /// the windows keeps the whole chunk representable.
    /// </summary>
    public IReadOnlyList<Encoded> EncodeWindows(string text, int maxTokens, double overlapFraction = 0.25)
    {
        var pieces = WordPieces(text);
        var budget = Math.Max(2, maxTokens) - 2;

        if (pieces.Count <= budget)
        {
            var single = new List<long>(pieces.Count + 2) { ClsId };
            single.AddRange(pieces);
            single.Add(SepId);
            return [new Encoded([.. single], new long[single.Count])];
        }

        var overlap = Math.Clamp((int)(budget * overlapFraction), 0, budget - 1);
        var stride = Math.Max(1, budget - overlap);

        var windows = new List<Encoded>();
        for (var start = 0; start < pieces.Count; start += stride)
        {
            var length = Math.Min(budget, pieces.Count - start);
            var ids = new List<long>(length + 2) { ClsId };
            ids.AddRange(pieces.GetRange(start, length));
            ids.Add(SepId);
            windows.Add(new Encoded([.. ids], new long[ids.Count]));
            if (start + length >= pieces.Count) break;
        }
        return windows;
    }

    /// <summary>Pair encoding for cross-encoders: [CLS] a [SEP] b [SEP], with segment ids.</summary>
    public Encoded EncodePair(string a, string b, int maxTokens)
    {
        var left = WordPieces(a);
        var right = WordPieces(b);
        var budget = Math.Max(3, maxTokens) - 3;

        // Truncate the longer sequence first, as the reference implementation does.
        while (left.Count + right.Count > budget)
        {
            if (right.Count >= left.Count && right.Count > 0) right.RemoveAt(right.Count - 1);
            else if (left.Count > 0) left.RemoveAt(left.Count - 1);
            else break;
        }

        var ids = new List<long>(left.Count + right.Count + 3);
        var types = new List<long>(ids.Capacity);
        ids.Add(ClsId); types.Add(0);
        foreach (var t in left) { ids.Add(t); types.Add(0); }
        ids.Add(SepId); types.Add(0);
        foreach (var t in right) { ids.Add(t); types.Add(1); }
        ids.Add(SepId); types.Add(1);
        return new Encoded([.. ids], [.. types]);
    }

    private List<long> WordPieces(string text)
    {
        var result = new List<long>();
        foreach (var word in BasicTokenize(text))
        {
            if (word.Length > MaxWordChars) { result.Add(UnkId); continue; }
            var start = 0;
            var subTokens = new List<long>();
            var ok = true;
            while (start < word.Length)
            {
                var end = word.Length;
                long found = -1;
                while (start < end)
                {
                    var piece = start == 0 ? word[start..end] : "##" + word[start..end];
                    if (_vocab.TryGetValue(piece, out var id)) { found = id; break; }
                    end--;
                }
                if (found < 0) { ok = false; break; }
                subTokens.Add(found);
                start = end;
            }
            if (ok) result.AddRange(subTokens);
            else result.Add(UnkId);
        }
        return result;
    }

    /// <summary>Lowercase, strip accents, split on whitespace and punctuation (BERT basic tokenizer).</summary>
    internal static List<string> BasicTokenize(string text)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();

        void Flush()
        {
            if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
        }

        foreach (var raw in text.Normalize(NormalizationForm.FormD))
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(raw);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (raw == '\0' || raw == '�') continue;

            var ch = char.ToLowerInvariant(raw);
            if (char.IsWhiteSpace(ch) || char.IsControl(ch)) { Flush(); continue; }
            if (IsPunctuation(ch)) { Flush(); tokens.Add(ch.ToString()); continue; }
            if (IsCjk(ch)) { Flush(); tokens.Add(ch.ToString()); continue; }
            sb.Append(ch);
        }
        Flush();
        return tokens;
    }

    private static bool IsPunctuation(char c)
    {
        if (c is >= '!' and <= '/' or >= ':' and <= '@' or >= '[' and <= '`' or >= '{' and <= '~') return true;
        return char.GetUnicodeCategory(c) is UnicodeCategory.ConnectorPunctuation
            or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation
            or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation;
    }

    private static bool IsCjk(char c) =>
        c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿'
          or >= '豈' and <= '﫿' or >= '぀' and <= 'ヿ';
}

public readonly record struct Encoded(long[] Ids, long[] TypeIds)
{
    public int Length => Ids.Length;
}
