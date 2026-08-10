using RagLadder.Api.Llm;
using RagLadder.Api.Models;

namespace RagLadder.Api.Vector;

/// <summary>The Qdrant payload (spec §5.3). Field names match the indexed payload keys exactly.</summary>
public sealed class ChunkPayload
{
    public required string ChunkId { get; init; }
    public required string DocId { get; init; }
    public required string Section { get; init; }
    public string? DocType { get; init; }
    public string? Subject { get; init; }
    public int? Year { get; init; }
    public string? Studio { get; init; }
    public string? Market { get; init; }
    public int Page { get; init; }
    public int Seq { get; init; }
    public required string Text { get; init; }
    public List<string> EntityKeys { get; init; } = [];

    public static ChunkPayload From(ChunkRecord c, string sectionHeading) => new()
    {
        ChunkId = c.Id,
        DocId = c.DocId,
        Section = sectionHeading,
        DocType = c.FrontMatter.DocType,
        Subject = c.FrontMatter.Subject,
        Year = c.FrontMatter.Year,
        Studio = c.FrontMatter.Studio,
        Market = c.FrontMatter.Market,
        Page = c.Page,
        Seq = c.Seq,
        Text = c.Text,
        EntityKeys = [.. c.EntityKeys],
    };

    public bool Matches(ChunkFilter f)
    {
        if (f.DocType is not null && !string.Equals(DocType, f.DocType, StringComparison.OrdinalIgnoreCase)) return false;
        if (f.Subject is not null && !string.Equals(Subject, f.Subject, StringComparison.OrdinalIgnoreCase)) return false;
        if (f.Studio is not null && !string.Equals(Studio, f.Studio, StringComparison.OrdinalIgnoreCase)) return false;
        if (f.Market is not null && !string.Equals(Market, f.Market, StringComparison.OrdinalIgnoreCase)) return false;
        if (f.Section is not null && !Section.Contains(f.Section, StringComparison.OrdinalIgnoreCase)) return false;
        if (f.Year is not null && Year != f.Year) return false;
        if (f.YearRange is { Length: 2 })
        {
            if (Year is null) return false;
            if (Year < f.YearRange[0] || Year > f.YearRange[1]) return false;
        }
        return true;
    }
}

public sealed record VectorPoint(string ChunkId, float[] Vector, ChunkPayload Payload);

public sealed record VectorHit(string ChunkId, double Score, ChunkPayload Payload);

public interface IVectorStore
{
    string Kind { get; }
    Task EnsureCollectionAsync(string collection, int dimensions, CancellationToken ct = default);
    Task DeleteCollectionAsync(string collection, CancellationToken ct = default);
    Task UpsertAsync(string collection, IReadOnlyList<VectorPoint> points, CancellationToken ct = default);
    Task<IReadOnlyList<VectorHit>> SearchAsync(string collection, float[] query, int limit, ChunkFilter? filter, CancellationToken ct = default);
    /// <summary>Full-text arm of hybrid search. Returns BM25-scored hits.</summary>
    Task<IReadOnlyList<VectorHit>> KeywordSearchAsync(string collection, string queryText, int limit, ChunkFilter? filter, CancellationToken ct = default);
    Task<int> CountAsync(string collection, CancellationToken ct = default);
    Task<ProviderHealth> HealthAsync(CancellationToken ct = default);
}

public static class CollectionNames
{
    public static string For(string docId, string strategy) => $"{docId}_{strategy}";
}
