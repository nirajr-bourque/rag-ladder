using Microsoft.AspNetCore.Mvc;
using RagLadder.Api.Infrastructure;
using RagLadder.Api.Models;
using RagLadder.Api.Pipeline;

namespace RagLadder.Api.Endpoints;

public sealed class ReviewDecision
{
    public List<string> Reject { get; set; } = [];
    public List<string> Accept { get; set; } = [];
    /// <summary>Reject every triple whose confidence is below this value.</summary>
    public double? RejectBelowConfidence { get; set; }
    public bool AcceptAll { get; set; }
}

public sealed class MergeDecisionRequest
{
    public string LeftKey { get; set; } = "";
    public string RightKey { get; set; } = "";
    /// <summary>merge | keep</summary>
    public string Decision { get; set; } = "keep";
}

public static class ReviewEndpoints
{
    public static void MapReviewEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/documents/{id}").WithTags("review");

        group.MapGet("/extraction", (string id, ReviewRepository review) =>
        {
            var result = review.GetExtraction(id);
            if (result is null) return Results.NotFound(new { error = "No proposed graph for this document. Process it first." });

            var rejected = review.GetRejections(id);
            return Results.Ok(new
            {
                result.DocId,
                result.Mode,
                result.Funnel,
                result.Metrics,
                result.Warnings,
                result.MergeCandidates,
                entities = result.Entities.OrderByDescending(e => e.MentionCount),
                relations = result.Relations.Select(r => new
                {
                    r.TripleHash, r.SubjectKey, r.ObjectKey, r.SubjectName, r.ObjectName,
                    r.SubjectType, r.ObjectType, r.Predicate, r.Confidence, r.Evidence,
                    r.MentionCount, r.ChunkIds, r.Flipped, r.Verdict, r.VerdictReason,
                    r.BelowFloor, r.Page, r.Properties,
                    rejected = rejected.Contains(r.TripleHash)
                }),
            });
        });

        group.MapGet("/extraction/metrics", (string id, ReviewRepository review) =>
        {
            var result = review.GetExtraction(id);
            if (result is null) return Results.NotFound(new { error = "No extraction results for this document." });
            return Results.Ok(new
            {
                result.Metrics,
                health = result.Metrics.Health(),
                funnel = result.Funnel,
            });
        });

        group.MapPost("/review/decisions", (string id, [FromBody] ReviewDecision decision, ReviewRepository review) =>
        {
            var result = review.GetExtraction(id);
            if (result is null) return Results.NotFound(new { error = "No proposed graph for this document." });

            var reject = new List<string>(decision.Reject);
            if (decision.RejectBelowConfidence is { } floor)
                reject.AddRange(result.Relations.Where(r => r.Confidence < floor).Select(r => r.TripleHash));

            if (decision.AcceptAll)
                review.RemoveRejections(id, result.Relations.Select(r => r.TripleHash));
            else if (decision.Accept.Count > 0)
                review.RemoveRejections(id, decision.Accept);

            if (reject.Count > 0) review.AddRejections(id, reject.Distinct());

            var rejected = review.GetRejections(id);
            return Results.Ok(new
            {
                rejectedCount = rejected.Count,
                acceptedCount = result.Relations.Count - rejected.Count,
            });
        });

        group.MapPost("/review/merge", (string id, [FromBody] MergeDecisionRequest request, ReviewRepository review) =>
        {
            review.SaveMergeDecision(id, request.LeftKey, request.RightKey,
                request.Decision == "merge" ? "merge" : "keep");
            return Results.Ok(new { saved = true, request.Decision });
        });

        group.MapPost("/graph/commit", async (
            string id, ProcessingService processing, CorpusRepository corpus, CancellationToken ct) =>
        {
            if (corpus.GetDocument(id) is null) return Results.NotFound(new { error = $"Unknown document '{id}'." });
            var job = processing.GetJobForDocument(id);
            var summary = await processing.CommitAsync(id, job, ct);
            return Results.Ok(summary);
        });
    }
}
