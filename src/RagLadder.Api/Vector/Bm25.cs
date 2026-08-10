using RagLadder.Api.Embedding;

namespace RagLadder.Api.Vector;

/// <summary>
/// BM25 over a small candidate set. The keyword arm of hybrid search exists so exact strings —
/// box office figures, production codes, award categories — can be found at all; embeddings are
/// blind to them (spec §0, trap 3). Numbers and currency are preserved as single tokens.
/// </summary>
public static class Bm25
{
    private const double K1 = 1.5;
    private const double B = 0.75;

    public static IReadOnlyList<(string Id, double Score)> Score(
        IReadOnlyList<(string Id, string Text)> documents,
        string query,
        int limit)
    {
        if (documents.Count == 0) return [];

        var queryTerms = Tokenize(query).Distinct().ToArray();
        if (queryTerms.Length == 0) return [];

        var tokenized = documents.Select(d => (d.Id, Terms: Tokenize(d.Text))).ToArray();
        var avgLen = tokenized.Average(d => (double)d.Terms.Count);
        if (avgLen <= 0) return [];

        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var doc in tokenized)
            foreach (var term in doc.Terms.Distinct())
                df[term] = df.GetValueOrDefault(term) + 1;

        var n = tokenized.Length;
        var results = new List<(string Id, double Score)>(n);

        foreach (var (id, terms) in tokenized)
        {
            var tf = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var t in terms) tf[t] = tf.GetValueOrDefault(t) + 1;

            double score = 0;
            foreach (var term in queryTerms)
            {
                if (!tf.TryGetValue(term, out var f)) continue;
                var docFreq = df.GetValueOrDefault(term, 1);
                var idf = Math.Log(1 + (n - docFreq + 0.5) / (docFreq + 0.5));
                var norm = f * (K1 + 1) / (f + K1 * (1 - B + B * terms.Count / avgLen));
                score += idf * norm;
            }
            if (score > 0) results.Add((id, score));
        }

        return [.. results.OrderByDescending(r => r.Score).Take(limit)];
    }

    /// <summary>
    /// Lowercased word tokens, but currency amounts and figures such as "$47.3M" or
    /// "1,240,000" survive whole — splitting them would defeat the point of the keyword arm.
    /// </summary>
    public static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '$' || char.IsDigit(c))
            {
                var start = i;
                if (c == '$') i++;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] is '.' or ',' )) i++;
                while (i < text.Length && char.IsLetter(text[i])) i++;   // trailing M / bn / LKR suffix
                var token = text[start..i].TrimEnd('.', ',');
                if (token.Length > 0) tokens.Add(token.ToLowerInvariant());
                continue;
            }
            if (char.IsLetter(c))
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '\'')) i++;
                var token = text[start..i].ToLowerInvariant();
                if (token.Length > 1 && !StopWords.Contains(token)) tokens.Add(token);
                continue;
            }
            i++;
        }
        return tokens;
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "was", "were", "with", "that", "this", "from", "are", "has", "have",
        "had", "its", "his", "her", "their", "which", "who", "whom", "what", "when", "where",
        "how", "did", "does", "into", "than", "then", "they", "them", "been", "being", "you",
        "your", "our", "not", "but", "all", "any", "can", "will", "would", "there", "here"
    };

    /// <summary>Tokens worth sending to a full-text index as an OR clause.</summary>
    public static IReadOnlyList<string> QueryTokens(string query) =>
        [.. Tokenize(query).Distinct().Take(24)];
}

/// <summary>Reciprocal rank fusion, k = 60 (spec §7.5 stage 4).</summary>
public static class Rrf
{
    public sealed record FusedHit(string Id, double Score, int? VectorRank, int? KeywordRank, double? VectorScore, double? KeywordScore)
    {
        public string Arm => (VectorRank, KeywordRank) switch
        {
            (not null, not null) => "both",
            (not null, null) => "vector",
            (null, not null) => "keyword",
            _ => "none"
        };
    }

    public static IReadOnlyList<FusedHit> Fuse(
        IReadOnlyList<VectorHit> vectorArm,
        IReadOnlyList<VectorHit> keywordArm,
        int k = 60)
    {
        var vectorRanks = vectorArm.Select((h, i) => (h, i)).ToDictionary(x => x.h.ChunkId, x => (Rank: x.i + 1, x.h.Score));
        var keywordRanks = keywordArm.Select((h, i) => (h, i)).ToDictionary(x => x.h.ChunkId, x => (Rank: x.i + 1, x.h.Score));

        var ids = vectorRanks.Keys.Union(keywordRanks.Keys, StringComparer.Ordinal);
        var fused = new List<FusedHit>();

        foreach (var id in ids)
        {
            var hasVector = vectorRanks.TryGetValue(id, out var v);
            var hasKeyword = keywordRanks.TryGetValue(id, out var kw);
            double score = 0;
            if (hasVector) score += 1.0 / (k + v.Rank);
            if (hasKeyword) score += 1.0 / (k + kw.Rank);
            fused.Add(new FusedHit(
                id, score,
                hasVector ? v.Rank : null,
                hasKeyword ? kw.Rank : null,
                hasVector ? v.Score : null,
                hasKeyword ? kw.Score : null));
        }

        return [.. fused.OrderByDescending(f => f.Score).ThenBy(f => f.Id, StringComparer.Ordinal)];
    }
}
