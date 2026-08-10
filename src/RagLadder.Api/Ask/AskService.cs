using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RagLadder.Api.Configuration;
using RagLadder.Api.Infrastructure;
using RagLadder.Api.Models;

namespace RagLadder.Api.Ask;

/// <summary>
/// Runs one rung of the ladder end to end.
///
/// Flow isolation is enforced here and is not negotiable (spec §7.4): the answer cache key covers
/// the document, the question and every resolved flag; no stage reuses another stage's retrieval;
/// there is no conversation history; and stage 0 is the only path that may answer unconstrained.
/// </summary>
public sealed class AskService(
    CorpusRepository corpus,
    CacheRepository cache,
    Retriever retriever,
    QueryRewriter rewriter,
    GraphStageService graphStage,
    AgenticLoop agentic,
    QueryRouter router,
    AnswerGenerator generator,
    IOptions<RagLadderOptions> options,
    ILogger<AskService> log)
{
    private readonly ConcurrentDictionary<string, AskResponse> _answerCache = new(StringComparer.Ordinal);
    private readonly RagLadderOptions _config = options.Value;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void ClearCache()
    {
        _answerCache.Clear();
        cache.ClearAnswers();
    }

    public int CachedAnswerCount => cache.AnswerCount();

    public IReadOnlyList<CachedAnswerInfo> CachedAnswers(string? docId = null) => cache.ListAnswers(docId);

    public async Task<AskResponse> AskAsync(AskRequest request, int? stage, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
            throw new ArgumentException("A question is required.", nameof(request));

        var document = corpus.GetDocument(request.DocumentId)
                       ?? throw new KeyNotFoundException($"Unknown document '{request.DocumentId}'.");

        var resolved = stage is { } s ? StagePresets.For(s, _config) : request.Options ?? new AskOptions();
        if (stage is null && request.Options is not null) Normalize(resolved);

        var cacheKey = AnswerGenerator.CacheScopeFor(document.Id, request.Question, resolved);

        // In-process first, then the durable table. The two hold the same envelope; the second
        // exists so a restart before the demo does not cost an hour of re-answering.
        if (_answerCache.TryGetValue(cacheKey, out var cached))
            return cached with { FromCache = true, GoldenId = request.GoldenId };

        if (Rehydrate(cacheKey) is { } stored)
        {
            _answerCache[cacheKey] = stored;
            return stored with { FromCache = true, GoldenId = request.GoldenId };
        }

        var response = await ExecuteAsync(document.Id, request, stage, resolved, cacheKey, ct);
        _answerCache[cacheKey] = response;
        Persist(cacheKey, document.Id, request.Question, stage, response);
        return response;
    }

    private AskResponse? Rehydrate(string cacheKey)
    {
        var payload = cache.GetAnswer(cacheKey);
        if (payload is null) return null;
        try
        {
            return JsonSerializer.Deserialize<AskResponse>(payload, Json);
        }
        catch (JsonException ex)
        {
            // A schema change between runs must degrade to a slow answer, never to a failed one.
            log.LogWarning(ex, "Discarding an unreadable cached answer for {Key}.", cacheKey);
            return null;
        }
    }

    private void Persist(string cacheKey, string docId, string question, int? stage, AskResponse response)
    {
        // A refusal caused by a transient provider failure would otherwise be cached as if it were
        // the document's answer, and would then need a manual cache clear to shake off.
        if (response.Warnings.Any(w => w.Contains("failed", StringComparison.OrdinalIgnoreCase))) return;

        try
        {
            cache.PutAnswer(cacheKey, docId, question, stage, JsonSerializer.Serialize(response, Json));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not persist the answer for {Key}; it stays in memory only.", cacheKey);
        }
    }

    private async Task<AskResponse> ExecuteAsync(
        string docId, AskRequest request, int? stage, AskOptions resolved, string cacheKey, CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        var warnings = new List<string>();
        var chatCalls = 0;
        long rewriteMs = 0, graphMs = 0;

        var stageName = stage is { } n ? StagePresets.Definition(n).Name : "custom";

        // ----- stage 0: no retrieval, no constraint --------------------------
        if (resolved.SkipRetrieval)
        {
            var unconstrained = await generator.UnconstrainedAsync(request.Question, ct);
            if (unconstrained.Warning is not null) warnings.Add(unconstrained.Warning);
            return new AskResponse
            {
                DocumentId = docId,
                Question = request.Question,
                GoldenId = request.GoldenId,
                Stage = stage,
                StageName = stageName,
                Answer = unconstrained.Answer,
                Refused = false,
                Unconstrained = true,
                Options = resolved,
                Prompt = unconstrained.Prompt,
                Warnings = [.. warnings, "Stage 0 is unconstrained: this answer comes from the model's training data, not from the document."],
                Timings = new TimingBlock { TotalMs = total.ElapsedMilliseconds, GenerateMs = unconstrained.ElapsedMs, ChatCalls = unconstrained.ChatCalls },
            };
        }

        // ----- stage 11: routing --------------------------------------------
        RouterBlock? routerBlock = null;
        if (resolved.UseRouter)
        {
            var routing = await router.RouteAsync(request.Question, resolved, ct);
            chatCalls += routing.Calls;
            if (routing.Warning is not null) warnings.Add(routing.Warning);
            routerBlock = routing.Block;
            resolved = routing.Options;
        }

        // ----- stage 6: query rewrite ----------------------------------------
        var searchText = request.Question;
        RewriteBlock? rewriteBlock = null;
        if (resolved.UseQueryRewrite)
        {
            var (block, ms, calls, warning) = await rewriter.RewriteAsync(request.Question, ct);
            rewriteBlock = block;
            searchText = block.Rewritten;
            rewriteMs = ms;
            chatCalls += calls;
            if (warning is not null) warnings.Add(warning);
        }

        // ----- stage 3: metadata filter --------------------------------------
        if (resolved.UseMetadataFilter && resolved.Filter.IsEmpty)
        {
            resolved.Filter = MetadataFilterInference.Infer(request.Question, corpus.GetSections(docId));
            if (!resolved.Filter.IsEmpty)
                warnings.Add($"Metadata filter inferred from the question: {Describe(resolved.Filter)}.");
        }

        // ----- retrieval ------------------------------------------------------
        RetrievalOutcome outcome;
        IReadOnlyList<AgenticStep> trace = [];

        if (resolved.UseAgentic)
        {
            var agenticResult = await agentic.RunAsync(docId, searchText, resolved, ct);
            chatCalls += agenticResult.Calls;
            trace = agenticResult.Trace;
            if (agenticResult.Warning is not null) warnings.Add(agenticResult.Warning);
            outcome = new RetrievalOutcome(agenticResult.Chunks, agenticResult.Chunks, 0, 0, agenticResult.ElapsedMs, 0);
        }
        else
        {
            outcome = await retriever.RetrieveAsync(docId, searchText, resolved, ct);
        }

        var context = outcome.Selected.ToList();

        // ----- stage 10: graph -------------------------------------------------
        GraphBlock? graphBlock = null;
        if (resolved.UseGraphExpansion)
        {
            var graphWatch = Stopwatch.StartNew();
            try
            {
                var graphOutcome = await graphStage.RunAsync(docId, request.Question, resolved, context, ct);
                graphBlock = graphOutcome.Block;
                chatCalls += graphOutcome.Calls;
                if (graphOutcome.Warning is not null) warnings.Add(graphOutcome.Warning);
                if (graphOutcome.Block.Note is not null) warnings.Add(graphOutcome.Block.Note);
                context.AddRange(graphOutcome.ExtraChunks);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Graph stage failed for {DocId}.", docId);
                warnings.Add($"Graph stage failed: {ex.Message}. The answer uses vector retrieval only.");
            }
            graphWatch.Stop();
            graphMs = graphWatch.ElapsedMilliseconds;
        }

        // ----- generation -------------------------------------------------------
        var generated = await generator.AnswerAsync(request.Question, context, graphBlock, resolved, cacheKey, ct);
        chatCalls += generated.ChatCalls;
        if (generated.Warning is not null) warnings.Add(generated.Warning);

        var answer = generated.Answer;
        var refused = generated.Refused;
        if (string.IsNullOrWhiteSpace(answer))
        {
            answer = generator.RefusalText;
            refused = true;
        }

        // ----- stage 8: citations -----------------------------------------------
        IReadOnlyList<Citation> citations = [];
        double? groundedness = null;
        if (resolved.RequireCitations && !refused)
        {
            var check = CitationChecker.Check(answer, context);
            citations = check.Citations;
            groundedness = check.Groundedness;
            if (check.Warning is not null) warnings.Add(check.Warning);
        }

        return new AskResponse
        {
            DocumentId = docId,
            Question = request.Question,
            GoldenId = request.GoldenId,
            Stage = stage,
            StageName = stageName,
            Answer = answer,
            Refused = refused,
            Options = resolved,
            Retrieval = new RetrievalBlock
            {
                Collection = resolved.Collection,
                TopK = resolved.TopK,
                CandidateK = resolved.CandidateK,
                CandidateCount = outcome.Candidates.Count,
                DroppedCount = outcome.DroppedCount,
                Hybrid = resolved.UseHybrid,
                Reranked = resolved.UseRerank,
                FilterApplied = resolved.UseMetadataFilter && !resolved.Filter.IsEmpty,
                Filter = resolved.UseMetadataFilter ? resolved.Filter : null,
                Chunks = context,
                Candidates = outcome.Candidates,
            },
            Rewrite = rewriteBlock,
            Graph = graphBlock,
            Trace = trace,
            Router = routerBlock,
            Citations = citations,
            Groundedness = groundedness,
            Prompt = generated.Prompt,
            Warnings = warnings,
            Timings = new TimingBlock
            {
                TotalMs = total.ElapsedMilliseconds,
                EmbedMs = outcome.EmbedMs,
                SearchMs = outcome.SearchMs,
                RerankMs = outcome.RerankMs,
                RewriteMs = rewriteMs,
                GraphMs = graphMs,
                GenerateMs = generated.ElapsedMs,
                ChatCalls = chatCalls,
            },
        };
    }

    private void Normalize(AskOptions o)
    {
        if (!ChunkStrategies.IsValid(o.Collection)) o.Collection = ChunkStrategies.Recursive;
        if (o.GraphMode is not (GraphModes.Expand or GraphModes.Path or GraphModes.Aggregate)) o.GraphMode = GraphModes.Expand;
        o.TopK = Math.Clamp(o.TopK <= 0 ? _config.Retrieval.TopK : o.TopK, 1, 50);
        o.CandidateK = Math.Clamp(o.CandidateK <= 0 ? o.TopK : o.CandidateK, o.TopK, 200);
        o.MaxPathHops = Math.Clamp(o.MaxPathHops <= 0 ? _config.Retrieval.MaxPathHops : o.MaxPathHops, 1, 20);
        o.MinEdgeConfidence = Math.Clamp(o.MinEdgeConfidence, 0, 1);
    }

    private static string Describe(ChunkFilter f)
    {
        var parts = new List<string>();
        if (f.DocType is not null) parts.Add($"docType={f.DocType}");
        if (f.Year is not null) parts.Add($"year={f.Year}");
        if (f.YearRange is { Length: 2 }) parts.Add($"year {f.YearRange[0]}-{f.YearRange[1]}");
        if (f.Subject is not null) parts.Add($"subject={f.Subject}");
        if (f.Studio is not null) parts.Add($"studio={f.Studio}");
        return string.Join(", ", parts);
    }
}
