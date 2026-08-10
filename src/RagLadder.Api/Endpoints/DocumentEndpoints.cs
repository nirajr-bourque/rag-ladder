using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RagLadder.Api.Configuration;
using RagLadder.Api.Infrastructure;
using RagLadder.Api.Models;
using RagLadder.Api.Parsing;
using RagLadder.Api.Pipeline;
using RagLadder.Api.Vector;

namespace RagLadder.Api.Endpoints;

public static class DocumentEndpoints
{
    public static void MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/documents").WithTags("documents");

        group.MapGet("", (CorpusRepository corpus) => Results.Ok(corpus.ListDocuments()));

        group.MapGet("/{id}", (string id, CorpusRepository corpus, ProcessingService processing) =>
        {
            var document = corpus.GetDocument(id);
            if (document is null) return Results.NotFound(new { error = $"Unknown document '{id}'." });

            var sections = corpus.GetSections(id);
            var chunks = corpus.GetChunks(id);
            return Results.Ok(new
            {
                document,
                sections = sections.Select(s => new
                {
                    s.Id, s.Ordinal, s.Heading, s.Page, s.FrontMatter, s.Summary,
                    length = s.Text.Length
                }),
                chunkCounts = chunks.GroupBy(c => c.Strategy).ToDictionary(g => g.Key, g => g.Count()),
                job = processing.GetJobForDocument(id),
            });
        });

        group.MapPost("/upload", async (
            HttpRequest request,
            CorpusRepository corpus,
            IOptions<RagLadderOptions> options,
            CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "Expected a multipart/form-data upload." });

            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "No file was uploaded." });
            if (!file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Only PDF files are supported. OCR is out of scope." });

            var docId = "doc_" + Guid.NewGuid().ToString("N")[..8];
            var directory = Path.Combine(options.Value.Storage.DataDirectory, "uploads");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, docId + ".pdf");

            await using (var stream = File.Create(path))
                await file.CopyToAsync(stream, ct);

            var document = new DocumentRecord
            {
                Id = docId,
                Title = Path.GetFileNameWithoutExtension(file.FileName),
                FileName = file.FileName,
                PageCount = 0,
                UploadedUtc = DateTimeOffset.UtcNow,
                Status = DocumentStatus.Uploaded,
            };
            corpus.UpsertDocument(document, null, path);
            return Results.Ok(document);
        }).DisableAntiforgery();

        /// Loads the committed demo corpus without an upload — the fastest path to a working demo.
        group.MapPost("/load-demo", (
            [FromQuery] string? path,
            CorpusRepository corpus,
            IOptions<RagLadderOptions> options) =>
        {
            var storage = options.Value.Storage;
            var demoDirectory = Path.Combine(storage.CorpusDirectory, "demo");

            // Explicit path wins; otherwise the configured demo PDF; otherwise whatever is there.
            var preferred = Path.Combine(demoDirectory, storage.DemoPdf);
            var candidate = path
                            ?? (File.Exists(preferred) ? preferred : null)
                            ?? (Directory.Exists(demoDirectory)
                                ? Directory.EnumerateFiles(demoDirectory, "*.pdf").FirstOrDefault()
                                : null);

            if (candidate is null || !File.Exists(candidate))
                return Results.NotFound(new
                {
                    error = $"No demo PDF found in '{demoDirectory}'. Build one first: " +
                            "dotnet run --project tools/RagLadder.CorpusBuilder -- " +
                            "--input spiderman-corpus-seed.md --output corpus/demo/spiderman-seed.pdf"
                });

            var docId = "doc_" + Hashing.Sha256Hex(Path.GetFullPath(candidate))[..8];
            var document = new DocumentRecord
            {
                Id = docId,
                Title = Path.GetFileNameWithoutExtension(candidate),
                FileName = Path.GetFileName(candidate),
                PageCount = 0,
                UploadedUtc = DateTimeOffset.UtcNow,
                Status = DocumentStatus.Uploaded,
            };
            corpus.UpsertDocument(document, null, Path.GetFullPath(candidate));
            return Results.Ok(document);
        });

        group.MapPost("/{id}/process", (
            string id,
            [FromBody] ProcessRequest? body,
            CorpusRepository corpus,
            ProcessingService processing) =>
        {
            if (corpus.GetDocument(id) is null) return Results.NotFound(new { error = $"Unknown document '{id}'." });
            var job = processing.Start(id, body ?? new ProcessRequest());
            return Results.Accepted($"/api/documents/{id}/status", new { jobId = job.JobId });
        });

        group.MapGet("/{id}/status", (string id, ProcessingService processing, CorpusRepository corpus) =>
        {
            var job = processing.GetJobForDocument(id);
            var document = corpus.GetDocument(id);
            if (document is null) return Results.NotFound(new { error = $"Unknown document '{id}'." });
            return Results.Ok(new
            {
                document.Status,
                document.GraphCommitted,
                job = job is null ? null : new
                {
                    job.JobId, job.Stage, job.StepIndex, job.StepCount, job.Progress, job.Message,
                    job.Completed, job.Failed, job.AwaitingReview, job.Warnings, job.StartedUtc, job.FinishedUtc
                }
            });
        });

        group.MapGet("/{id}/chunks", (
            string id, [FromQuery] string? strategy, [FromQuery] int? skip, [FromQuery] int? take,
            CorpusRepository corpus) =>
        {
            var chunks = corpus.GetChunks(id, ChunkStrategies.IsValid(strategy) ? strategy : null);
            var page = chunks.Skip(skip ?? 0).Take(Math.Clamp(take ?? 50, 1, 500));
            return Results.Ok(new { total = chunks.Count, chunks = page });
        });

        group.MapDelete("/{id}", async (
            string id,
            CorpusRepository corpus,
            IVectorStore vectors,
            Graph.IGraphStore graph,
            Ask.AskService ask,
            CancellationToken ct) =>
        {
            if (corpus.GetDocument(id) is null) return Results.NotFound(new { error = $"Unknown document '{id}'." });

            foreach (var strategy in ChunkStrategies.All)
                await vectors.DeleteCollectionAsync(CollectionNames.For(id, strategy), ct);
            await graph.DeleteDocumentAsync(id, ct);
            corpus.DeleteDocument(id);
            ask.ClearCache();
            return Results.Ok(new { deleted = id });
        });
    }
}

/// <summary>Surfaces a parse failure as an actionable message rather than a stack trace (spec §12).</summary>
public sealed class ParseExceptionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (ScannedPdfException ex)
        {
            context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            await context.Response.WriteAsJsonAsync(new { error = ex.Message });
        }
    }
}
