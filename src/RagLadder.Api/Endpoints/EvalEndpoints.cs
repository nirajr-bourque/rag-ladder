using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RagLadder.Api.Ask;
using RagLadder.Api.Configuration;
using RagLadder.Api.Eval;
using RagLadder.Api.Infrastructure;
using RagLadder.Api.Models;

namespace RagLadder.Api.Endpoints;

public sealed class EvalRequest
{
    public List<int> Stages { get; set; } = [];
    public List<string> QuestionIds { get; set; } = [];
}

public static class EvalEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapEvalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/documents/{id}").WithTags("eval");

        group.MapGet("/golden", (string id, ReviewRepository review) =>
        {
            var set = review.GetGolden(id);
            return set is null
                ? Results.NotFound(new { error = "No golden set loaded for this document. POST /golden/load to attach one." })
                : Results.Ok(set);
        });

        group.MapPut("/golden", (string id, [FromBody] GoldenSet set, ReviewRepository review) =>
        {
            set.DocumentId = id;
            review.SaveGolden(id, set);
            return Results.Ok(new { set.Name, questions = set.Questions.Count });
        });

        /// Attaches the hand-authored demo golden set shipped in corpus/demo/golden.json.
        group.MapPost("/golden/load", (
            string id, [FromQuery] string? path, ReviewRepository review, IOptions<RagLadderOptions> options) =>
        {
            // Default to the golden set matching the configured demo PDF, so loading the seed
            // corpus does not silently grade it against the full dossier's questions.
            var demoDirectory = Path.Combine(options.Value.Storage.CorpusDirectory, "demo");
            var matching = Path.Combine(demoDirectory,
                Path.GetFileNameWithoutExtension(options.Value.Storage.DemoPdf) switch
                {
                    "spiderman-seed" => "golden-spiderman.json",
                    _ => "golden.json"
                });

            var file = path
                       ?? (File.Exists(matching) ? matching : null)
                       ?? Path.Combine(demoDirectory, "golden.json");

            if (!File.Exists(file))
                return Results.NotFound(new { error = $"Golden set not found at '{file}'." });

            var set = JsonSerializer.Deserialize<GoldenSet>(File.ReadAllText(file), Json);
            if (set is null) return Results.BadRequest(new { error = "Golden set could not be parsed." });

            set.DocumentId = id;
            review.SaveGolden(id, set);
            return Results.Ok(new
            {
                set.Name,
                questions = set.Questions.Count,
                byType = set.Questions.GroupBy(q => q.Type).ToDictionary(g => g.Key, g => g.Count()),
            });
        });

        group.MapPost("/golden/generate", async (
            string id, [FromQuery] int? perSection, EvalService eval, ReviewRepository review, CancellationToken ct) =>
        {
            var generated = await eval.GenerateAsync(id, Math.Clamp(perSection ?? 2, 1, 5), ct);
            var existing = review.GetGolden(id);
            if (existing is not null)
            {
                existing.Questions.AddRange(generated.Questions);
                review.SaveGolden(id, existing);
            }
            else
            {
                review.SaveGolden(id, generated);
            }
            return Results.Ok(new
            {
                generated = generated.Questions.Count,
                warning = "Generated questions are weaker evidence than the hand-authored set: a question " +
                          "generated from a chunk is biased toward being retrievable by that chunk. " +
                          "Use the hand-authored set for the presentation."
            });
        });

        group.MapPost("/eval", (string id, [FromBody] EvalRequest? body, EvalService eval) =>
        {
            var request = body ?? new EvalRequest();
            var stages = request.Stages.Count > 0
                ? request.Stages
                : Enumerable.Range(0, StagePresets.MaxStage + 1).ToList();
            var run = eval.Start(id, stages, request.QuestionIds);
            return Results.Accepted($"/api/eval/{run.RunId}", new { run.RunId, run.Stages });
        });

        group.MapGet("/eval/runs", (string id, EvalService eval) =>
            Results.Ok(eval.ListRuns(id).Select(r => new
            {
                r.RunId, r.StartedUtc, r.FinishedUtc, r.Completed, r.Stages,
                questions = r.Cells.Select(c => c.QuestionId).Distinct().Count(),
                overall = r.OverallByStage,
            })));

        app.MapGet("/api/eval/{runId}", (string runId, EvalService eval) =>
        {
            var run = eval.GetRun(runId);
            return run is null ? Results.NotFound(new { error = "Unknown eval run." }) : Results.Ok(run);
        }).WithTags("eval");
    }
}
