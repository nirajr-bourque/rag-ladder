using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using RagLadder.Api.Chunking;
using RagLadder.Api.Configuration;
using RagLadder.Api.Embedding;
using RagLadder.Api.Extraction;
using RagLadder.Api.Graph;
using RagLadder.Api.Infrastructure;
using RagLadder.Api.Llm;
using RagLadder.Api.Models;
using RagLadder.Api.Parsing;
using RagLadder.Api.Vector;

namespace RagLadder.Api.Pipeline;

/// <summary>
/// The eleven-step processing pipeline (spec §5). Step 9 is a genuine pause: processing stops and
/// waits for a human to approve the proposed graph before anything is written to Neo4j.
/// </summary>
public sealed class ProcessingService(
    CorpusRepository corpus,
    ReviewRepository review,
    IVectorStore vectors,
    IGraphStore graph,
    IEmbedder embedder,
    IChatClient chat,
    ExtractionService extraction,
    PdfDocumentParser parser,
    SectionSegmenter segmenter,
    IOptions<RagLadderOptions> options,
    ILogger<ProcessingService> log)
{
    private static readonly string[] StepNames =
    [
        "Parse", "Segment", "Chunk", "Embed", "Enrich", "Extract",
        "Resolve", "Verify", "Review", "Commit", "Derive"
    ];

    private readonly ConcurrentDictionary<string, ProcessingJob> _jobs = new();
    private readonly RagLadderOptions _config = options.Value;

    public ProcessingJob? GetJob(string jobId) => _jobs.GetValueOrDefault(jobId);

    public ProcessingJob? GetJobForDocument(string docId) =>
        _jobs.Values.Where(j => j.DocId == docId).MaxBy(j => j.StartedUtc);

    public ProcessingJob Start(string docId, ProcessRequest request)
    {
        var job = new ProcessingJob { JobId = "job_" + Guid.NewGuid().ToString("N")[..10], DocId = docId };
        _jobs[job.JobId] = job;

        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(job, request, job.Cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                job.Failed = true;
                job.Message = "Cancelled.";
                job.FinishedUtc = DateTimeOffset.UtcNow;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Processing failed for {DocId}.", docId);
                job.Failed = true;
                job.Message = ex.Message;
                job.Warnings.Add(ex.Message);
                job.FinishedUtc = DateTimeOffset.UtcNow;
                corpus.SetStatus(docId, DocumentStatus.Failed);
            }
        });

        return job;
    }

    private void Step(ProcessingJob job, int index, string message)
    {
        job.StepIndex = index;
        job.Stage = StepNames[index];
        job.Message = message;
        job.Progress = Math.Round((double)index / StepNames.Length, 3);
    }

    private async Task RunAsync(ProcessingJob job, ProcessRequest request, CancellationToken ct)
    {
        var docId = job.DocId;
        var document = corpus.GetDocument(docId) ?? throw new InvalidOperationException($"Unknown document '{docId}'.");
        corpus.SetStatus(docId, DocumentStatus.Processing);

        if (embedder is CachingEmbedder caching) caching.ResetCounters();

        // ----- 1. Parse -----------------------------------------------------
        Step(job, 0, "Extracting text, page numbers and headings.");
        var pdfPath = corpus.GetPdfPath(docId) ?? throw new InvalidOperationException("The uploaded PDF is missing from disk.");
        ParsedDocument parsed;
        await using (var stream = File.OpenRead(pdfPath))
            parsed = parser.Parse(stream);

        corpus.UpsertDocument(document with { PageCount = parsed.Pages.Count }, parsed.Text);
        if (parsed.RemovedRunningLines.Count > 0)
            job.Warnings.Add($"Stripped {parsed.RemovedRunningLines.Count} running header/footer line pattern(s).");

        // ----- 2. Segment ---------------------------------------------------
        Step(job, 1, "Inferring sections from font sizes and front matter blocks.");
        var sections = segmenter.Segment(docId, parsed);
        if (sections.Count == 0) throw new InvalidOperationException("No sections could be inferred from this document.");
        corpus.ReplaceSections(docId, sections);

        var withFrontMatter = sections.Count(s => s.FrontMatter.DocType is not null);
        job.Warnings.Add($"{sections.Count} sections; {withFrontMatter} carry a parsed front matter block.");

        // ----- 3. Chunk (fixed + recursive) ---------------------------------
        Step(job, 2, "Chunking with three strategies.");
        var seq = 0;
        var chunks = new List<ChunkRecord>();
        chunks.AddRange(BuildPageChunks(docId, parsed, sections, new FixedChunker(_config.Chunking), ref seq));
        chunks.AddRange(BuildChunks(docId, sections, new RecursiveChunker(_config.Chunking), ref seq, null));

        // ----- 4. Embed and index -------------------------------------------
        Step(job, 3, "Embedding and indexing (local ONNX, batched, cached).");
        await IndexAsync(docId, chunks, sections, ct);

        // ----- 5. Enrich, then build and index the contextual collection -----
        Step(job, 4, request.SkipSectionSummaries
            ? "Building deterministic section summaries (model calls skipped)."
            : "Summarising sections for the contextual collection.");
        var enriched = await EnrichAsync(docId, sections, job, request.SkipSectionSummaries, ct);
        var recursiveChunker = new RecursiveChunker(_config.Chunking);
        var contextual = BuildChunks(docId, enriched, recursiveChunker, ref seq, ContextualPrefix.Build);
        chunks.AddRange(contextual);
        corpus.ReplaceChunks(docId, chunks);
        await IndexAsync(docId, contextual, enriched, ct);

        var embedCalls = embedder is CachingEmbedder c2 ? c2.ComputedCount : -1;
        job.Warnings.Add(embedCalls == 0
            ? "Warm cache: zero embedder calls for this document."
            : $"{embedCalls} embedding(s) computed, {(embedder as CachingEmbedder)?.CacheHitCount ?? 0} served from cache.");

        if (request.SkipExtraction)
        {
            Step(job, 10, "Extraction skipped; vector collections are ready.");
            corpus.SetStatus(docId, DocumentStatus.Committed);
            Finish(job, "Processed without graph extraction.");
            return;
        }

        // ----- 6-8. Extract, resolve, verify ---------------------------------
        Step(job, 5, "Extracting entities and relations.");
        var sourceChunks = chunks.Where(x => x.Strategy == _config.Extraction.SourceStrategy).ToList();
        var sectionsById = enriched.ToDictionary(s => s.Id, s => s, StringComparer.Ordinal);
        var rejections = review.GetRejections(docId);

        var progress = new Progress<ExtractionProgress>(p =>
        {
            job.Message = p.Message;
            var within = p.Total == 0 ? 0 : 0.25 * p.Processed / p.Total;
            job.Progress = Math.Round(5.0 / StepNames.Length + within, 3);
        });

        var result = await extraction.ExtractAsync(docId, sourceChunks, sectionsById, request, rejections, progress, ct);

        Step(job, 6, $"Resolved {result.Entities.Count} entities (merge ratio {result.Metrics.EntityMergeRatio:F2}).");
        Step(job, 7, result.Mode == "thorough"
            ? $"Verified {result.Relations.Count} triples."
            : "Quick mode: verification skipped.");

        foreach (var warning in result.Warnings) job.Warnings.Add(warning);
        review.SaveExtraction(result);

        // ----- 9. Review gate -------------------------------------------------
        if (!request.SkipReview)
        {
            Step(job, 8, $"Awaiting review: {result.Entities.Count} entities, {result.Relations.Count} relations proposed.");
            job.AwaitingReview = true;
            job.Progress = 0.8;
            corpus.SetStatus(docId, DocumentStatus.AwaitingReview);
            return;
        }

        Step(job, 8, "Review skipped by request.");
        await CommitAsync(docId, job, ct);
        Finish(job, "Committed without review.");
    }

    /// <summary>
    /// Resumes a paused job at step 10. Called by POST /api/documents/{id}/graph/commit — the
    /// review gate is a real halt, not a cosmetic one.
    /// </summary>
    public async Task<CommitSummary> CommitAsync(string docId, ProcessingJob? job, CancellationToken ct)
    {
        var result = review.GetExtraction(docId)
                     ?? throw new InvalidOperationException("There is no proposed graph for this document. Process it first.");
        var document = corpus.GetDocument(docId) ?? throw new InvalidOperationException($"Unknown document '{docId}'.");
        var rejected = review.GetRejections(docId);

        var accepted = result.Relations.Where(r => !rejected.Contains(r.TripleHash)).ToList();
        var keptKeys = accepted.SelectMany(r => new[] { r.SubjectKey, r.ObjectKey }).ToHashSet(StringComparer.Ordinal);
        var entities = result.Entities.Where(e => keptKeys.Contains(e.Key) || e.MentionCount > 0).ToList();

        if (job is not null) Step(job, 9, $"Committing {entities.Count} nodes and {accepted.Count} edges.");

        await graph.EnsureSchemaAsync(ct);
        var chunks = corpus.GetChunks(docId);
        var sections = corpus.GetSections(docId);

        await graph.CommitAsync(new GraphCommit
        {
            Document = document,
            Sections = sections,
            Chunks = chunks,
            Entities = entities,
            Relations = accepted,
        }, ct);

        // Chunk payloads carry the resolved entity keys so the vector side can see them too.
        var keysByChunk = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var entity in entities)
            foreach (var chunkId in entity.ChunkIds)
            {
                if (!keysByChunk.TryGetValue(chunkId, out var list)) keysByChunk[chunkId] = list = new List<string>();
                ((List<string>)list).Add(entity.Key);
            }
        corpus.SetEntityKeys(keysByChunk);

        if (job is not null) Step(job, 10, "Computing derived collaboration edges.");
        var derivedCount = await graph.ComputeDerivedEdgesAsync(docId, ct);

        result.Metrics.HumanRejectionRate = result.Relations.Count == 0
            ? 0
            : (double)rejected.Count / result.Relations.Count;
        review.SaveExtraction(result);
        corpus.SetStatus(docId, DocumentStatus.Committed, graphCommitted: true);

        if (job is not null)
        {
            job.AwaitingReview = false;
            Finish(job, $"Committed {entities.Count} nodes, {accepted.Count} edges, {derivedCount} derived.");
        }

        return new CommitSummary(entities.Count, accepted.Count, rejected.Count, derivedCount);
    }

    private void Finish(ProcessingJob job, string message)
    {
        job.Completed = true;
        job.Progress = 1;
        job.Message = message;
        job.FinishedUtc = DateTimeOffset.UtcNow;
    }

    // ----- helpers --------------------------------------------------------

    /// <summary>
    /// The fixed strategy is the deliberately bad baseline, so it is blind to document structure:
    /// it walks the extracted text page by page and cuts every 400 tokens with no overlap, exactly
    /// as a naive "extract page, chunk page" pipeline does. That blindness is what breaks trap 1 —
    /// a filmography that straddles a page boundary can only ever be seen half at a time — and
    /// watching stage 2 repair it is the point of the rung.
    /// </summary>
    private List<ChunkRecord> BuildPageChunks(
        string docId,
        ParsedDocument parsed,
        IReadOnlyList<SectionRecord> sections,
        IChunker chunker,
        ref int seq)
    {
        var chunks = new List<ChunkRecord>();
        var ordinal = 0;

        for (var pageIndex = 0; pageIndex < parsed.PageStartOffsets.Count; pageIndex++)
        {
            var start = parsed.PageStartOffsets[pageIndex];
            var end = pageIndex + 1 < parsed.PageStartOffsets.Count
                ? parsed.PageStartOffsets[pageIndex + 1]
                : parsed.Text.Length;
            if (end <= start) continue;

            foreach (var span in chunker.Split(parsed.Text[start..end], start))
            {
                var section = SectionForSpan(sections, span.Start, span.End);
                chunks.Add(new ChunkRecord
                {
                    Id = $"{docId}#{seq}",
                    DocId = docId,
                    Strategy = chunker.Strategy,
                    Seq = seq,
                    StrategyOrdinal = ordinal,
                    SectionId = section?.Id ?? sections[0].Id,
                    Page = pageIndex + 1,
                    StartChar = span.Start,
                    EndChar = span.End,
                    Text = span.Text,
                    RawText = span.Text,
                    FrontMatter = section?.FrontMatter ?? FrontMatter.Empty,
                });
                seq++;
                ordinal++;
            }
        }
        return chunks;
    }

    /// <summary>The section a structure-blind chunk overlaps most, for metadata and provenance.</summary>
    private static SectionRecord? SectionForSpan(IReadOnlyList<SectionRecord> sections, int start, int end) =>
        sections
            .Where(s => s.StartChar < end && s.EndChar > start)
            .MaxBy(s => Math.Min(s.EndChar, end) - Math.Max(s.StartChar, start));

    private List<ChunkRecord> BuildChunks(
        string docId,
        IReadOnlyList<SectionRecord> sections,
        IChunker chunker,
        ref int seq,
        Func<FrontMatter, string?, string>? prefixBuilder)
    {
        var strategy = prefixBuilder is null ? chunker.Strategy : ChunkStrategies.Contextual;
        var chunks = new List<ChunkRecord>();
        var ordinal = 0;

        foreach (var section in sections)
        {
            foreach (var span in chunker.Split(section.Text, section.StartChar))
            {
                var prefix = prefixBuilder?.Invoke(section.FrontMatter, section.Summary) ?? "";
                chunks.Add(new ChunkRecord
                {
                    Id = $"{docId}#{seq}",
                    DocId = docId,
                    Strategy = strategy,
                    Seq = seq,
                    StrategyOrdinal = ordinal,
                    SectionId = section.Id,
                    Page = section.Page,
                    StartChar = span.Start,
                    EndChar = span.End,
                    Text = prefix + span.Text,
                    RawText = span.Text,
                    FrontMatter = section.FrontMatter,
                });
                seq++;
                ordinal++;
            }
        }
        return chunks;
    }

    private async Task IndexAsync(string docId, IReadOnlyList<ChunkRecord> chunks,
        IReadOnlyList<SectionRecord> sections, CancellationToken ct)
    {
        var headings = sections.ToDictionary(s => s.Id, s => s.Heading, StringComparer.Ordinal);

        foreach (var group in chunks.GroupBy(c => c.Strategy))
        {
            var collection = CollectionNames.For(docId, group.Key);
            var ordered = group.OrderBy(c => c.StrategyOrdinal).ToList();
            var embeddings = await embedder.EmbedAsync([.. ordered.Select(c => c.Text)], ct);

            // Create the collection from the width the embedder actually returned, not from a
            // configured constant: an Ollama-served model may be 768 or 1024 dimensions.
            var width = embeddings.Count > 0 ? embeddings[0].Length : embedder.Dimensions;
            await vectors.EnsureCollectionAsync(collection, width, ct);

            var points = ordered.Select((c, i) => new VectorPoint(
                c.Id, embeddings[i], ChunkPayload.From(c, headings.GetValueOrDefault(c.SectionId, "")))).ToList();

            await vectors.UpsertAsync(collection, points, ct);
        }
    }

    /// <summary>One LLM call per section produces the summary prepended to contextual chunks.</summary>
    private async Task<List<SectionRecord>> EnrichAsync(
        string docId, IReadOnlyList<SectionRecord> sections, ProcessingJob job,
        bool skipSummaries, CancellationToken ct)
    {
        var enriched = new List<SectionRecord>(sections.Count);
        var failures = 0;

        if (skipSummaries)
        {
            foreach (var section in sections)
            {
                var summary = Fallback(section);
                corpus.SetSectionSummary(section.Id, summary);
                enriched.Add(section with { Summary = summary });
            }
            job.Warnings.Add(
                $"Section summaries were generated deterministically from headings for all {sections.Count} sections — " +
                "no model calls. The contextual prefix still names the work and its year.");
            return enriched;
        }

        foreach (var section in sections)
        {
            ct.ThrowIfCancellationRequested();
            var body = section.Text.Length > 4000 ? section.Text[..4000] : section.Text;

            var response = await chat.CompleteAsync(new ChatRequest
            {
                Model = chat.ChatModel,
                Messages =
                [
                    ChatMessage.System(ExtractionPrompts.SectionSummarySystem()),
                    ChatMessage.User($"Heading: {section.Heading}\nSubject: {section.FrontMatter.Subject ?? "unknown"}\n\n{body}")
                ],
                Temperature = 0,
                Purpose = ChatPurpose.SectionSummary,
            }, ct);

            string summary;
            if (response.Failed || string.IsNullOrWhiteSpace(response.Content))
            {
                failures++;
                // Fall back to a deterministic summary rather than leaving the prefix empty:
                // trap 6 depends on the contextual prefix naming the work.
                summary = Fallback(section);
            }
            else
            {
                summary = response.Content.Trim().ReplaceLineEndings(" ");
            }

            corpus.SetSectionSummary(section.Id, summary);
            enriched.Add(section with { Summary = summary });
        }

        if (failures > 0)
            job.Warnings.Add($"{failures} section summaries fell back to a deterministic heading-based summary because the model call failed.");

        return enriched;
    }

    private static string Fallback(SectionRecord section)
    {
        var fm = section.FrontMatter;
        var parts = new List<string> { section.Heading };
        if (fm.Subject is not null) parts.Add($"about {fm.Subject}");
        if (fm.Year is not null) parts.Add($"({fm.Year})");
        if (fm.DocType is not null) parts.Add($"[{fm.DocType}]");
        return string.Join(' ', parts);
    }
}

public sealed record CommitSummary(int Nodes, int Edges, int Rejected, int DerivedEdges);
