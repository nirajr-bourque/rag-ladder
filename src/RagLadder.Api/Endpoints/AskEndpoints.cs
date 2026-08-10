using Microsoft.AspNetCore.Mvc;
using RagLadder.Api.Ask;
using RagLadder.Api.Infrastructure;
using RagLadder.Api.Models;

namespace RagLadder.Api.Endpoints;

public sealed class CompareRequest
{
    public string DocumentId { get; set; } = "";
    public string Question { get; set; } = "";
    public string? GoldenId { get; set; }
    public List<int> Stages { get; set; } = [];
}

public static class AskEndpoints
{
    public static void MapAskEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/stages", () => Results.Ok(StagePresets.Definitions.Select(d => new
        {
            d.Number, d.Name, d.Teaches, d.OptionSummary, d.TrapsFixed
        }))).WithTags("ask");

        app.MapPost("/api/ask", async ([FromBody] AskRequest request, AskService ask, CancellationToken ct) =>
            Results.Ok(await ask.AskAsync(request, null, ct))).WithTags("ask");

        app.MapPost("/api/ask/stage/{n:int}", async (
            int n, [FromBody] AskRequest request, AskService ask, CancellationToken ct) =>
        {
            if (n is < 0 or > StagePresets.MaxStage)
                return Results.BadRequest(new { error = $"Stage must be between 0 and {StagePresets.MaxStage}." });
            return Results.Ok(await ask.AskAsync(request, n, ct));
        }).WithTags("ask");

        // Runs the same question at several rungs so they can be shown side by side. Each rung is
        // executed independently — no stage reuses another's retrieval or answer (spec §7.4).
        app.MapPost("/api/compare", async ([FromBody] CompareRequest request, AskService ask, CancellationToken ct) =>
        {
            var stages = request.Stages.Count > 0
                ? request.Stages.Where(s => s is >= 0 and <= StagePresets.MaxStage).Distinct().OrderBy(s => s).ToList()
                : [1, 2];

            var results = new List<AskResponse>();
            foreach (var stage in stages)
                results.Add(await ask.AskAsync(new AskRequest
                {
                    DocumentId = request.DocumentId,
                    Question = request.Question,
                    GoldenId = request.GoldenId,
                }, stage, ct));

            return Results.Ok(new { request.Question, stages, results });
        }).WithTags("ask");

        // ----- warm cache ----------------------------------------------------

        // What is already warm, so the Ask tab can say so before a demo rather than after.
        app.MapGet("/api/ask/cache", (AskService ask, [FromQuery] string? documentId) =>
            Results.Ok(new
            {
                limit = CacheRepository.AnswerCacheLimit,
                count = ask.CachedAnswerCount,
                answers = ask.CachedAnswers(documentId),
            })).WithTags("ask");

        app.MapDelete("/api/ask/cache", (AskService ask) =>
        {
            ask.ClearCache();
            return Results.Ok(new { cleared = true });
        }).WithTags("ask");

        // Answers one question at every rung and leaves all twelve in the durable cache. Cold, this
        // is twenty-odd minutes on a CPU model; afterwards each rung replays instantly, which is the
        // only way the ladder is watchable in front of an audience. Runs sequentially on purpose —
        // parallel requests evict each other's prompt prefix in Ollama and take longer overall.
        app.MapPost("/api/ask/warm", async (
            [FromBody] WarmRequest request, AskService ask, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Question))
                return Results.BadRequest(new { error = "A question is required." });

            var stages = request.Stages.Count > 0
                ? request.Stages.Where(s => s is >= 0 and <= StagePresets.MaxStage).Distinct().OrderBy(s => s).ToList()
                : Enumerable.Range(0, StagePresets.MaxStage + 1).ToList();

            var results = new List<object>();
            foreach (var stage in stages)
            {
                var started = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var response = await ask.AskAsync(new AskRequest
                    {
                        DocumentId = request.DocumentId,
                        Question = request.Question,
                    }, stage, ct);

                    results.Add(new
                    {
                        stage,
                        stageName = response.StageName,
                        response.Answer,
                        response.FromCache,
                        ms = started.ElapsedMilliseconds,
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One rung failing must not cost the eleven that already succeeded.
                    results.Add(new { stage, error = ex.Message, ms = started.ElapsedMilliseconds });
                }
            }

            return Results.Ok(new { request.Question, warmed = results.Count, results });
        }).WithTags("ask");
    }
}

public sealed class WarmRequest
{
    public string DocumentId { get; set; } = "";
    public string Question { get; set; } = "";
    public List<int> Stages { get; set; } = [];
}
