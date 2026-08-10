using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using RagLadder.Api.Ask;
using RagLadder.Api.Extraction;
using RagLadder.Api.Infrastructure;
using RagLadder.Api.Llm;
using RagLadder.Api.Models;
using RagLadder.Api.Vector;

namespace RagLadder.Api.Eval;

/// <summary>
/// Runs the golden set across the ladder.
///
/// The breakdown is per question type, not just overall: the overall curve is smooth and teaches
/// nothing, while the per-type heatmap shows stage 4 fixing exact_figure while doing nothing at
/// all for path. Regressions are collected deliberately — at least one rung usually makes
/// something worse, and that belongs on a slide (spec §8).
/// </summary>
public sealed class EvalService(
    AskService ask,
    ReviewRepository review,
    CorpusRepository corpus,
    IChatClient chat,
    ILogger<EvalService> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, EvalRun> _runs = new(StringComparer.Ordinal);

    public EvalRun? GetRun(string runId) => _runs.GetValueOrDefault(runId) ?? review.GetEvalRun(runId);
    public IReadOnlyList<EvalRun> ListRuns(string docId) => review.ListEvalRuns(docId);

    public EvalRun Start(string docId, IReadOnlyList<int> stages, IReadOnlyList<string>? questionIds)
    {
        var golden = review.GetGolden(docId)
                     ?? throw new InvalidOperationException("No golden set is loaded for this document.");

        var questions = questionIds is { Count: > 0 }
            ? golden.Questions.Where(q => questionIds.Contains(q.Id)).ToList()
            : golden.Questions;

        var run = new EvalRun
        {
            RunId = "eval_" + Guid.NewGuid().ToString("N")[..10],
            DocumentId = docId,
            StartedUtc = DateTimeOffset.UtcNow,
            Stages = [.. stages.Distinct().OrderBy(s => s)],
        };
        _runs[run.RunId] = run;

        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(run, questions, CancellationToken.None);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Eval run {RunId} failed.", run.RunId);
                run.Error = ex.Message;
                run.Completed = true;
                review.SaveEvalRun(run);
            }
        });

        return run;
    }

    private async Task RunAsync(EvalRun run, IReadOnlyList<GoldenQuestion> questions, CancellationToken ct)
    {
        var total = Math.Max(1, questions.Count * run.Stages.Count);
        var done = 0;

        foreach (var question in questions)
        {
            foreach (var stage in run.Stages)
            {
                ct.ThrowIfCancellationRequested();
                var watch = Stopwatch.StartNew();
                EvalCell cell;

                try
                {
                    var response = await ask.AskAsync(new AskRequest
                    {
                        DocumentId = run.DocumentId,
                        Question = question.Question,
                        GoldenId = question.Id,
                    }, stage, ct);

                    cell = Grade(question, stage, response, watch.ElapsedMilliseconds);
                    foreach (var warning in response.Warnings.Where(w => w.Contains("failed", StringComparison.OrdinalIgnoreCase)))
                        if (!run.Warnings.Contains(warning)) run.Warnings.Add(warning);
                }
                catch (Exception ex)
                {
                    cell = new EvalCell
                    {
                        QuestionId = question.Id, Type = question.Type, Stage = stage,
                        Pass = false, Failure = ex.Message, ElapsedMs = watch.ElapsedMilliseconds
                    };
                }

                run.Cells.Add(cell);
                done++;
                run.Progress = Math.Round((double)done / total, 3);
            }
        }

        Summarize(run);
        run.Completed = true;
        run.FinishedUtc = DateTimeOffset.UtcNow;
        review.SaveEvalRun(run);
    }

    /// <summary>
    /// Grading is deliberately mechanical: expected substrings, retrieval recall against expected
    /// sections, and — for the control group — a strict refusal check. No model-as-judge, because
    /// a judge would blur exactly the failures the ladder is meant to expose.
    /// </summary>
    private EvalCell Grade(GoldenQuestion question, int stage, AskResponse response, long elapsedMs)
    {
        var retrieved = response.Retrieval?.Chunks ?? [];
        var recall = ComputeRecall(question, retrieved);

        bool pass;
        string? failure = null;

        if (question.Type == QuestionTypes.Ungrounded || question.ExpectRefusal)
        {
            // Stage 0 must answer; every other stage must refuse. This group is the honesty check
            // for the whole demo (spec §8).
            pass = stage == 0 ? !response.Refused && response.Answer.Length > 0 : response.Refused;
            if (!pass)
                failure = stage == 0
                    ? "Stage 0 refused a question it should have answered from parametric knowledge."
                    : "Answered a question about material absent from the corpus instead of refusing.";
        }
        else if (question.Type == QuestionTypes.Path && question.ExpectedPathContains.Count > 0)
        {
            var pathNames = response.Graph?.Path?.Nodes.Select(n => n.Name).ToList() ?? [];
            var missing = question.ExpectedPathContains
                .Where(expected => !pathNames.Any(n => n.Contains(expected, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            pass = pathNames.Count > 0 && missing.Count == 0;
            if (!pass)
                failure = pathNames.Count == 0
                    ? "No path was computed."
                    : $"Path missing expected waypoint(s): {string.Join(", ", missing)}.";
        }
        else
        {
            var missing = question.ExpectedAnswerContains
                .Where(expected => !ContainsLoose(response.Answer, expected))
                .ToList();
            pass = !response.Refused && question.ExpectedAnswerContains.Count > 0 && missing.Count == 0;
            if (response.Refused) failure = "Refused.";
            else if (question.ExpectedAnswerContains.Count == 0) failure = "No expected answer defined; not gradeable.";
            else if (missing.Count > 0) failure = $"Answer missing: {string.Join(" | ", missing)}.";
        }

        return new EvalCell
        {
            QuestionId = question.Id,
            Type = question.Type,
            Stage = stage,
            Pass = pass,
            Refused = response.Refused,
            RetrievalRecall = recall,
            Answer = response.Answer,
            Failure = failure,
            ElapsedMs = elapsedMs,
        };
    }

    private static double ComputeRecall(GoldenQuestion question, IReadOnlyList<RetrievedChunk> retrieved)
    {
        if (question.ExpectedChunkIds.Count > 0)
        {
            var hit = question.ExpectedChunkIds.Count(id => retrieved.Any(c => c.ChunkId == id));
            return (double)hit / question.ExpectedChunkIds.Count;
        }
        if (question.ExpectedSections.Count > 0)
        {
            var hit = question.ExpectedSections.Count(s =>
                retrieved.Any(c => c.Section.Contains(s, StringComparison.OrdinalIgnoreCase)
                                   || c.Text.Contains(s, StringComparison.OrdinalIgnoreCase)));
            return (double)hit / question.ExpectedSections.Count;
        }
        return retrieved.Count > 0 ? 1 : 0;
    }

    /// <summary>Case- and punctuation-insensitive containment, so "$47.3M" matches "$47.3 M".</summary>
    private static bool ContainsLoose(string answer, string expected)
    {
        var a = ExtractionFilters.NormalizeForGrounding(answer).Replace(" ", "");
        var e = ExtractionFilters.NormalizeForGrounding(expected).Replace(" ", "");
        return e.Length > 0 && a.Contains(e, StringComparison.Ordinal);
    }

    private static void Summarize(EvalRun run)
    {
        foreach (var stage in run.Stages)
        {
            var cells = run.Cells.Where(c => c.Stage == stage).ToList();
            run.OverallByStage[stage] = cells.Count == 0 ? 0 : Math.Round((double)cells.Count(c => c.Pass) / cells.Count, 3);
        }

        foreach (var typeGroup in run.Cells.GroupBy(c => c.Type))
        {
            var byStage = new Dictionary<int, double>();
            foreach (var stage in run.Stages)
            {
                var cells = typeGroup.Where(c => c.Stage == stage).ToList();
                byStage[stage] = cells.Count == 0 ? 0 : Math.Round((double)cells.Count(c => c.Pass) / cells.Count, 3);
            }
            run.HeatmapByType[typeGroup.Key] = byStage;
        }

        // A rung that makes something worse is worth finding and showing.
        foreach (var questionGroup in run.Cells.GroupBy(c => c.QuestionId))
        {
            var ordered = questionGroup.OrderBy(c => c.Stage).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                if (!ordered[i - 1].Pass || ordered[i].Pass) continue;
                run.Regressions.Add(new EvalRegression
                {
                    QuestionId = questionGroup.Key,
                    Type = ordered[i].Type,
                    FromStage = ordered[i - 1].Stage,
                    ToStage = ordered[i].Stage,
                    Note = ordered[i].Failure ?? "Passed at the earlier stage and failed at the later one.",
                });
            }
        }
    }

    // ----- golden set generation ------------------------------------------

    /// <summary>
    /// Auto-generation for uploaded PDFs. Every generated question is marked as such, because a
    /// question generated from a chunk is biased toward being retrievable by that chunk — weaker
    /// evidence than the hand-authored set, and the UI must say so (spec §8).
    /// </summary>
    public async Task<GoldenSet> GenerateAsync(string docId, int perSection, CancellationToken ct)
    {
        var sections = corpus.GetSections(docId).Where(s => s.Text.Length > 400).Take(20).ToList();
        var set = new GoldenSet { Name = $"generated-{docId}", DocumentId = docId };
        var index = 0;

        foreach (var section in sections)
        {
            ct.ThrowIfCancellationRequested();
            var response = await chat.CompleteAsync(new ChatRequest
            {
                Model = chat.ChatModel,
                Messages =
                [
                    ChatMessage.System(
                        $"Write {perSection} factual questions answerable only from the passage, each with the exact " +
                        "answer substring copied from the passage. Prefer questions about names, figures and dates. " +
                        "Return JSON only: {\"questions\":[{\"question\":\"...\",\"answer\":\"...\"}]}"),
                    ChatMessage.User(section.Text.Length > 3000 ? section.Text[..3000] : section.Text)
                ],
                Temperature = 0,
                JsonOnly = true,
                Purpose = ChatPurpose.GoldenGeneration,
            }, ct);

            if (response.Failed) continue;
            var parsed = JsonText.TryDeserialize<GeneratedQuestions>(response.Content, Json);
            if (parsed?.Questions is null) continue;

            foreach (var q in parsed.Questions)
            {
                if (string.IsNullOrWhiteSpace(q.Question) || string.IsNullOrWhiteSpace(q.Answer)) continue;
                set.Questions.Add(new GoldenQuestion
                {
                    Id = $"gen{index++:000}",
                    Question = q.Question!.Trim(),
                    Type = QuestionTypes.SimpleLookup,
                    ExpectedAnswerContains = [q.Answer!.Trim()],
                    ExpectedSections = [section.Heading],
                    Generated = true,
                    Notes = "Generated from a chunk, so biased toward being retrievable by that chunk. Weaker evidence than the hand-authored set.",
                });
            }
        }

        return set;
    }

    private sealed class GeneratedQuestions
    {
        public List<GeneratedQuestion>? Questions { get; set; }
    }

    private sealed class GeneratedQuestion
    {
        public string? Question { get; set; }
        public string? Answer { get; set; }
    }
}
