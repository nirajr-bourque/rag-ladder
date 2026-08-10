using Microsoft.AspNetCore.Mvc;
using RagLadder.Api.Graph;
using RagLadder.Api.Infrastructure;
using RagLadder.Api.Models;

namespace RagLadder.Api.Endpoints;

public static class GraphEndpoints
{
    public static void MapGraphEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/documents/{id}/graph").WithTags("graph");

        group.MapGet("", async (
            string id,
            [FromQuery] double? minConfidence,
            [FromQuery] bool? includeDerived,
            [FromQuery] int? limit,
            IGraphStore graph,
            CancellationToken ct) =>
            Results.Ok(await graph.SnapshotAsync(id,
                Math.Clamp(minConfidence ?? 0.0, 0, 1),
                includeDerived ?? true,
                Math.Clamp(limit ?? 400, 1, 3000), ct)));

        group.MapGet("/entities", async (
            string id, [FromQuery] string? type, [FromQuery] string? q, [FromQuery] int? limit,
            IGraphStore graph, CancellationToken ct) =>
            Results.Ok(await graph.SearchEntitiesAsync(id, type, q, Math.Clamp(limit ?? 100, 1, 1000), ct)));

        group.MapGet("/edge", async (
            string id, [FromQuery] string from, [FromQuery] string predicate, [FromQuery] string to,
            IGraphStore graph, CorpusRepository corpus, CancellationToken ct) =>
        {
            var edge = await graph.GetEdgeAsync(id, from, predicate, to, ct);
            if (edge is null) return Results.NotFound(new { error = "No such edge." });

            // Clicking an edge should show its evidence span and its source chunk (spec §9).
            var chunks = corpus.GetChunksByIds(edge.ChunkIds)
                .Select(c => new { c.Id, c.Page, c.SectionId, text = c.RawText })
                .ToList();
            return Results.Ok(new { edge, chunks });
        });

        // The path mode on its own, for the Explore tab's Connect button.
        group.MapGet("/path", async (
            string id,
            [FromQuery] string from,
            [FromQuery] string to,
            [FromQuery] int? maxHops,
            [FromQuery] double? minConfidence,
            IGraphStore graph,
            CancellationToken ct) =>
        {
            var path = await graph.ShortestPathAsync(id, from, to,
                Math.Clamp(maxHops ?? 6, 1, 20), Math.Clamp(minConfidence ?? 0.0, 0, 1), ct);
            return path is null
                ? Results.Ok(new { found = false, message = "No path of that length connects these two through credited work." })
                : Results.Ok(new { found = true, path });
        });

        group.MapGet("/aggregate", async (
            string id,
            [FromQuery] string preset,
            [FromQuery] int? year,
            [FromQuery] double? minConfidence,
            IGraphStore graph,
            CancellationToken ct) =>
        {
            if (!AggregationPresets.All.Any(p => p.Id == preset))
                return Results.BadRequest(new
                {
                    error = $"Unknown preset '{preset}'.",
                    available = AggregationPresets.All.Select(p => new { p.Id, p.Title })
                });

            return Results.Ok(await graph.AggregateAsync(id, preset, year,
                Math.Clamp(minConfidence ?? 0.0, 0, 1), ct));
        });

        app.MapGet("/api/graph/presets", () => Results.Ok(
            AggregationPresets.All.Select(p => new { p.Id, p.Title, cypher = AggregationCypher.For(p.Id) })))
            .WithTags("graph");

        app.MapGet("/api/ontology", (Ontology ontology) => Results.Ok(new
        {
            ontology.Version,
            ontology.NodeTypes,
            ontology.RelationTypes,
            ontology.Genres,
            creditPredicates = Ontology.CreditPredicates,
        })).WithTags("graph");
    }
}
