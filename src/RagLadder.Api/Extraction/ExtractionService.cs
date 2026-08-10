using System.Text.Json;
using Microsoft.Extensions.Options;
using RagLadder.Api.Configuration;
using RagLadder.Api.Infrastructure;
using RagLadder.Api.Llm;
using RagLadder.Api.Models;

namespace RagLadder.Api.Extraction;

public sealed record ExtractionProgress(int Processed, int Total, string Message);

/// <summary>
/// Orchestrates LLM extraction over a document's chunks and runs the deterministic filter chain.
/// The design principle throughout: an LLM proposes, deterministic code disposes.
/// </summary>
public sealed class ExtractionService(
    IChatClient chat,
    CacheRepository cache,
    Ontology ontology,
    EntityResolver resolver,
    IOptions<RagLadderOptions> options,
    ILogger<ExtractionService> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ExtractionOptions _config = options.Value.Extraction;

    public async Task<ExtractionResult> ExtractAsync(
        string docId,
        IReadOnlyList<ChunkRecord> chunks,
        IReadOnlyDictionary<string, SectionRecord> sections,
        ProcessRequest request,
        HashSet<string> priorRejections,
        IProgress<ExtractionProgress>? progress,
        CancellationToken ct = default)
    {
        var mode = string.IsNullOrWhiteSpace(request.Mode) ? _config.DefaultMode : request.Mode.ToLowerInvariant();
        var thorough = mode == "thorough";
        var result = new ExtractionResult { DocId = docId, Mode = mode };
        var funnel = result.Funnel;
        var metrics = result.Metrics;

        var selected = SelectChunks(chunks, request, result.Warnings);
        metrics.ChunksProcessed = selected.Count;

        var rawEntities = new List<(ProposedEntity Entity, string ChunkId)>();
        var rawRelations = new List<ProposedRelation>();
        var chunkTextById = selected.ToDictionary(c => c.Id, c => c.RawText, StringComparer.Ordinal);
        var byOrdinal = chunks.ToDictionary(c => c.StrategyOrdinal, c => c);

        var processed = 0;
        foreach (var chunk in selected)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ExtractionProgress(processed, selected.Count, $"Extracting chunk {processed + 1}/{selected.Count}"));
            processed++;

            var section = sections.GetValueOrDefault(chunk.SectionId);
            var previousTail = byOrdinal.TryGetValue(chunk.StrategyOrdinal - 1, out var prev)
                ? Tail(prev.RawText, _config.PreviousChunkTailChars)
                : null;

            var extraction = await ExtractChunkAsync(chunk, section, previousTail, metrics, result.Warnings, ct);
            if (extraction is null) { metrics.SkippedChunks++; continue; }

            funnel.Extracted += extraction.Relations.Count;
            AccumulateChunk(chunk, section, extraction, rawEntities, rawRelations, funnel);
        }

        // ----- filter 4: dangling references --------------------------------
        var entityKeys = rawEntities.Select(e => e.Entity.Key).ToHashSet(StringComparer.Ordinal);
        var beforeDangling = rawRelations.Count;
        rawRelations = [.. rawRelations.Where(r => entityKeys.Contains(r.SubjectKey) && entityKeys.Contains(r.ObjectKey))];
        funnel.Drop("dangling-reference", beforeDangling - rawRelations.Count);
        funnel.NonDangling = rawRelations.Count;

        // ----- filter 5: confidence floor (soft) ----------------------------
        foreach (var r in rawRelations)
            r.BelowFloor = r.Confidence < _config.ConfidenceFloor;

        // ----- filter 6: entity resolution ----------------------------------
        var merged = MergeEntities(rawEntities);
        var roleClusters = BuildRoleClusters(rawRelations);
        var resolution = await resolver.ResolveAsync(merged, roleClusters, ct);
        funnel.Resolved = resolution.Entities.Count;

        foreach (var r in rawRelations)
        {
            if (resolution.KeyRemap.TryGetValue(r.SubjectKey, out var s)) r.SubjectKey = s;
            if (resolution.KeyRemap.TryGetValue(r.ObjectKey, out var o)) r.ObjectKey = o;
        }
        var canonicalNames = resolution.Entities.ToDictionary(e => e.Key, e => e.Name, StringComparer.Ordinal);
        foreach (var r in rawRelations)
        {
            if (canonicalNames.TryGetValue(r.SubjectKey, out var sn)) r.SubjectName = sn;
            if (canonicalNames.TryGetValue(r.ObjectKey, out var on)) r.ObjectName = on;
        }

        // Self-loops can appear once two surface forms resolve to the same node.
        var beforeSelfLoop = rawRelations.Count;
        rawRelations = [.. rawRelations.Where(r => r.SubjectKey != r.ObjectKey)];
        funnel.Drop("self-loop-after-resolution", beforeSelfLoop - rawRelations.Count);

        // ----- filter 7: deduplication ---------------------------------------
        var deduplicated = ExtractionFilters.Deduplicate(rawRelations);
        funnel.Drop("duplicate-triple", rawRelations.Count - deduplicated.Count);
        funnel.Deduplicated = deduplicated.Count;

        // Rejections persist by triple hash so reprocessing does not resurface them.
        var beforeRejections = deduplicated.Count;
        deduplicated = [.. deduplicated.Where(r => !priorRejections.Contains(r.TripleHash))];
        funnel.Drop("previously-rejected", beforeRejections - deduplicated.Count);

        // ----- verification pass (thorough only) ------------------------------
        if (thorough)
            await VerifyAsync(deduplicated, chunkTextById, metrics, result.Warnings, progress, ct);
        funnel.Verified = deduplicated.Count;

        result.Entities.AddRange(resolution.Entities);
        result.Relations.AddRange(deduplicated.OrderByDescending(r => r.Confidence));
        result.MergeCandidates.AddRange(resolution.AmbiguousMerges);

        ComputeMetrics(metrics, funnel, result, resolution, selected.Count);
        return result;
    }

    // ----- bring your own model -------------------------------------------

    /// <summary>
    /// The cache key for one chunk's extraction. Exposed so an externally produced extraction —
    /// pasted through a more capable model than the one running locally — can be seeded into the
    /// cache and then flow through the ordinary pipeline. Nothing downstream changes: the same
    /// seven filters, the same funnel, the same review gate.
    /// </summary>
    public string CacheKeyFor(ChunkRecord chunk, IReadOnlyDictionary<int, ChunkRecord> byOrdinal)
    {
        var previousTail = byOrdinal.TryGetValue(chunk.StrategyOrdinal - 1, out var prev)
            ? Tail(prev.RawText, _config.PreviousChunkTailChars)
            : null;
        return CacheRepository.ExtractionKey(
            chunk.RawText + (previousTail ?? ""), _config.OntologyVersion, chat.ExtractionModel);
    }

    /// <summary>The exact user message the model would have been sent for this chunk.</summary>
    public string UserPromptFor(ChunkRecord chunk, SectionRecord? section, IReadOnlyDictionary<int, ChunkRecord> byOrdinal)
    {
        var previousTail = byOrdinal.TryGetValue(chunk.StrategyOrdinal - 1, out var prev)
            ? Tail(prev.RawText, _config.PreviousChunkTailChars)
            : null;
        return ExtractionPrompts.User(chunk.RawText, chunk.FrontMatter, section?.Summary, previousTail);
    }

    public string SystemPrompt() => ExtractionPrompts.System(ontology) + "\n\n" + ExtractionPrompts.FewShot();

    /// <summary>Writes an externally produced extraction into the cache for one chunk.</summary>
    public void SeedCache(string cacheKey, RawExtraction extraction) =>
        cache.PutExtraction(cacheKey, JsonSerializer.Serialize(extraction, Json));

    // ----- one chunk ------------------------------------------------------

    private async Task<RawExtraction?> ExtractChunkAsync(
        ChunkRecord chunk, SectionRecord? section, string? previousTail,
        ExtractionMetrics metrics, List<string> warnings, CancellationToken ct)
    {
        var cacheKey = CacheRepository.ExtractionKey(
            chunk.RawText + (previousTail ?? ""), _config.OntologyVersion, chat.ExtractionModel);

        if (cache.GetExtraction(cacheKey) is { } cached)
        {
            metrics.CachedChunks++;
            var fromCache = Deserialize(cached);
            if (fromCache is not null) return fromCache;
        }

        var messages = new List<ChatMessage>
        {
            ChatMessage.System(ExtractionPrompts.System(ontology) + "\n\n" + ExtractionPrompts.FewShot()),
            ChatMessage.User(ExtractionPrompts.User(chunk.RawText, chunk.FrontMatter, section?.Summary, previousTail))
        };

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var response = await chat.CompleteAsync(new ChatRequest
            {
                Model = chat.ExtractionModel,
                Messages = attempt == 0
                    ? messages
                    : [.. messages, ChatMessage.User("Your previous reply was not valid JSON. Reply with the JSON object only — no prose, no code fences.")],
                Temperature = 0,
                JsonOnly = true,
                Purpose = ChatPurpose.Extraction,
            }, ct);

            if (!response.FromCache) metrics.ChatCalls++;

            if (response.Failed)
            {
                warnings.Add($"Chunk {chunk.Id}: extraction call failed — {response.Warning}");
                return null;
            }

            var parsed = Deserialize(response.Content);
            if (parsed is not null)
            {
                cache.PutExtraction(cacheKey, response.Content);
                return parsed;
            }

            log.LogWarning("Chunk {ChunkId}: malformed extraction JSON on attempt {Attempt}.", chunk.Id, attempt + 1);
        }

        // Per-chunk failures are non-fatal: skip, warn, continue (spec §12).
        warnings.Add($"Chunk {chunk.Id}: skipped after two malformed JSON responses.");
        return null;
    }

    private static RawExtraction? Deserialize(string content)
    {
        var json = JsonText.ExtractObject(content);
        if (json is null) return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<RawExtraction>(json, Json);
            return parsed is null ? null : parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Applies filters 1 to 3 to one chunk's output and accumulates what survives.</summary>
    private void AccumulateChunk(
        ChunkRecord chunk, SectionRecord? section, RawExtraction extraction,
        List<(ProposedEntity, string)> entities, List<ProposedRelation> relations,
        ExtractionFunnel funnel)
    {
        var workSlug = InferWorkSlug(extraction, chunk, section);
        var byName = new Dictionary<string, ProposedEntity>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in extraction.Entities)
        {
            if (string.IsNullOrWhiteSpace(raw.Name)) continue;

            // Filter 2: ontology conformance — do not coerce, drop.
            if (!ontology.IsNodeType(raw.Type)) { funnel.Drop("entity-nonconformant-type"); continue; }

            // Filter 1: evidence grounding, with repair when the name is genuinely in the text.
            var evidence = raw.Evidence;
            if (!ExtractionFilters.IsGrounded(evidence, chunk.RawText))
            {
                if (!ExtractionFilters.TryRepairEvidence(chunk.RawText, [raw.Name.Trim()], out var repairedEvidence))
                {
                    funnel.Drop("entity-ungrounded");
                    continue;
                }
                evidence = repairedEvidence;
                funnel.Drop("entity-evidence-repaired");
            }

            var year = raw.Year ?? (raw.Type == "Film" ? chunk.FrontMatter.Year : null);
            var scope = raw.Type == "Character" ? workSlug : null;
            var key = EntityKey.Build(raw.Type, raw.Name.Trim(), year, scope);

            var entity = new ProposedEntity
            {
                Key = key,
                Name = raw.Name.Trim(),
                Type = raw.Type,
                Year = year,
                WorkSlug = scope,
                MentionCount = 1,
                Evidence = evidence,
            };
            entity.ChunkIds.Add(chunk.Id);
            entities.Add((entity, chunk.Id));
            byName[raw.Name.Trim()] = entity;
        }

        foreach (var raw in extraction.Relations)
        {
            if (string.IsNullOrWhiteSpace(raw.Subject) || string.IsNullOrWhiteSpace(raw.Object)) continue;

            var predicate = ExtractionFilters.NormalizePredicate(raw.Predicate);
            if (!ontology.IsPredicate(predicate))
            {
                // Name the offender. "The model proposed APPEARED_IN, which is not in our
                // ontology" is a far better thing to show in the funnel than a bare count.
                funnel.Drop("relation-nonconformant-predicate");
                funnel.Drop("rejected-predicate:" + (predicate.Length == 0 ? "(empty)" : predicate));
                continue;
            }
            var relationEvidence = raw.Evidence;
            if (!ExtractionFilters.IsGrounded(relationEvidence, chunk.RawText))
            {
                // Same repair as for entities: if both endpoint names are genuinely present, the
                // model paraphrased rather than fabricated.
                if (!ExtractionFilters.TryRepairEvidence(chunk.RawText,
                        [raw.Subject.Trim(), raw.Object.Trim()], out var repairedRelation))
                {
                    funnel.Drop("relation-ungrounded");
                    continue;
                }
                relationEvidence = repairedRelation;
                funnel.Drop("relation-evidence-repaired");
            }
            funnel.Grounded++;

            if (!byName.TryGetValue(raw.Subject.Trim(), out var subject) ||
                !byName.TryGetValue(raw.Object.Trim(), out var obj))
            {
                // Endpoints must appear in the same response's entities array.
                funnel.Drop("relation-endpoint-missing");
                continue;
            }

            var relation = new ProposedRelation
            {
                SubjectKey = subject.Key, ObjectKey = obj.Key,
                SubjectName = subject.Name, ObjectName = obj.Name,
                SubjectType = subject.Type, ObjectType = obj.Type,
                Predicate = predicate,
                Confidence = Math.Clamp(raw.Confidence, 0, 1),
                Evidence = relationEvidence,
                Properties = raw.Properties ?? [],
                Page = chunk.Page,
            };
            relation.ChunkIds.Add(chunk.Id);

            // Filter 3: direction and endpoint types.
            var outcome = ExtractionFilters.CheckDirection(relation, ontology);
            if (!outcome.Keep) { funnel.Drop(outcome.DropReason ?? "direction-invalid"); continue; }
            if (outcome.Flipped) funnel.Flipped++;

            funnel.Conformant++;
            relations.Add(relation);
        }
    }

    /// <summary>Characters are scoped to a work; prefer a work named in the same response.</summary>
    private static string InferWorkSlug(RawExtraction extraction, ChunkRecord chunk, SectionRecord? section)
    {
        var work = extraction.Entities.FirstOrDefault(e => e.Type is "Film" or "TVSeries");
        if (work is not null) return EntityKey.Slug(work.Name);
        var subject = chunk.FrontMatter.Subject ?? section?.FrontMatter.Subject;
        return subject is not null ? EntityKey.Slug(subject) : "unscoped";
    }

    private static List<ProposedEntity> MergeEntities(List<(ProposedEntity Entity, string ChunkId)> raw)
    {
        var byKey = new Dictionary<string, ProposedEntity>(StringComparer.Ordinal);
        foreach (var (entity, _) in raw)
        {
            if (!byKey.TryGetValue(entity.Key, out var existing))
            {
                byKey[entity.Key] = entity;
                continue;
            }
            existing.MentionCount += entity.MentionCount;
            existing.Year ??= entity.Year;
            foreach (var chunkId in entity.ChunkIds) existing.ChunkIds.Add(chunkId);
        }
        return [.. byKey.Values];
    }

    /// <summary>
    /// Role/year signature per entity, used by resolution rule 4 to refuse an automatic merge when
    /// two similar names hold incompatible roles in disjoint years.
    /// </summary>
    private static Dictionary<string, IReadOnlyList<string>> BuildRoleClusters(IEnumerable<ProposedRelation> relations)
    {
        var clusters = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var r in relations)
        {
            void Add(string key, string token)
            {
                if (!clusters.TryGetValue(key, out var set)) clusters[key] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(token);
            }
            Add(r.SubjectKey, r.Predicate);
            Add(r.ObjectKey, r.Predicate);
            if (r.Properties.TryGetValue("year", out var year))
            {
                Add(r.SubjectKey, "y:" + year);
                Add(r.ObjectKey, "y:" + year);
            }
        }
        return clusters.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)[.. kv.Value], StringComparer.Ordinal);
    }

    // ----- verification ---------------------------------------------------

    private async Task VerifyAsync(
        List<ProposedRelation> relations,
        IReadOnlyDictionary<string, string> chunkText,
        ExtractionMetrics metrics,
        List<string> warnings,
        IProgress<ExtractionProgress>? progress,
        CancellationToken ct)
    {
        var byChunk = relations
            .GroupBy(r => r.ChunkIds.FirstOrDefault() ?? "", StringComparer.Ordinal)
            .Where(g => chunkText.ContainsKey(g.Key))
            .ToList();

        var done = 0;
        var supported = 0;
        var judged = 0;

        foreach (var group in byChunk)
        {
            foreach (var batch in group.Chunk(_config.VerificationBatchSize))
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new ExtractionProgress(done, byChunk.Count, "Verifying extracted triples"));

                var response = await chat.CompleteAsync(new ChatRequest
                {
                    Model = chat.ExtractionModel,
                    Messages =
                    [
                        ChatMessage.System(ExtractionPrompts.VerificationSystem()),
                        ChatMessage.User(ExtractionPrompts.VerificationUser(chunkText[group.Key], batch))
                    ],
                    Temperature = 0,
                    JsonOnly = true,
                    Purpose = ChatPurpose.Verification,
                }, ct);

                if (!response.FromCache) metrics.ChatCalls++;

                if (response.Failed)
                {
                    warnings.Add($"Verification failed for chunk {group.Key} — {response.Warning}. Triples kept unverified.");
                    continue;
                }

                var verdicts = ParseVerdicts(response.Content);
                if (verdicts is null)
                {
                    warnings.Add($"Verification returned malformed JSON for chunk {group.Key}. Triples kept unverified.");
                    continue;
                }

                foreach (var verdict in verdicts)
                {
                    if (verdict.Index < 0 || verdict.Index >= batch.Length) continue;
                    var relation = batch[verdict.Index];
                    relation.Verdict = verdict.Verdict?.ToUpperInvariant() ?? "SUPPORTED";
                    relation.VerdictReason = verdict.Reason;
                    judged++;

                    switch (relation.Verdict)
                    {
                        case "SUPPORTED":
                            supported++;
                            break;
                        case "PARTIAL":
                            relation.Confidence *= _config.PartialConfidenceMultiplier;
                            relation.BelowFloor = relation.Confidence < _config.ConfidenceFloor;
                            break;
                        default:
                            relation.Verdict = "UNSUPPORTED";
                            break;
                    }
                }
            }
            done++;
        }

        var dropped = relations.RemoveAll(r => r.Verdict == "UNSUPPORTED");
        metrics.VerificationPassRate = judged == 0 ? 1 : (double)supported / judged;
        if (dropped > 0) warnings.Add($"Verification dropped {dropped} unsupported triple(s).");
    }

    private static List<VerdictRow>? ParseVerdicts(string content)
    {
        var json = JsonText.ExtractObject(content);
        if (json is null) return null;
        try
        {
            return JsonSerializer.Deserialize<VerdictEnvelope>(json, Json)?.Verdicts;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class VerdictEnvelope
    {
        public List<VerdictRow> Verdicts { get; set; } = [];
    }

    private sealed class VerdictRow
    {
        public int Index { get; set; }
        public string? Verdict { get; set; }
        public string? Reason { get; set; }
    }

    // ----- chunk selection and metrics ------------------------------------

    /// <summary>
    /// Honours the extraction chunk cap. When the cap bites, sampling is spread across the
    /// document rather than truncating at the front, and the shortfall is always reported —
    /// silent truncation would read as full coverage (spec §4.1).
    /// </summary>
    private List<ChunkRecord> SelectChunks(IReadOnlyList<ChunkRecord> chunks, ProcessRequest request, List<string> warnings)
    {
        var cap = request.ChunkCap ?? _config.ChunkCap;
        if (cap <= 0 || chunks.Count <= cap) return [.. chunks];

        if (!request.SpreadSampling)
        {
            warnings.Add($"Extraction capped at the first {cap} of {chunks.Count} chunks. {chunks.Count - cap} chunks were not extracted.");
            return [.. chunks.Take(cap)];
        }

        var step = (double)chunks.Count / cap;
        var sampled = new List<ChunkRecord>(cap);
        for (var i = 0; i < cap; i++) sampled.Add(chunks[(int)(i * step)]);
        warnings.Add($"Extraction capped at {cap} of {chunks.Count} chunks, sampled evenly across the document. " +
                     $"{chunks.Count - cap} chunks were not extracted, so the graph is incomplete by design.");
        return sampled;
    }

    private void ComputeMetrics(
        ExtractionMetrics metrics, ExtractionFunnel funnel, ExtractionResult result,
        ResolutionOutcome resolution, int chunkCount)
    {
        var proposedRelations = Math.Max(1, funnel.Extracted);
        metrics.GroundingPassRate = (double)funnel.Grounded / proposedRelations;
        metrics.ConformanceRate = funnel.Grounded == 0 ? 1 : (double)funnel.Conformant / funnel.Grounded;
        metrics.DirectionFlipRate = funnel.Conformant == 0 ? 0 : (double)funnel.Flipped / funnel.Conformant;
        metrics.EntityMergeRatio = resolution.Entities.Count == 0
            ? 0
            : (double)resolution.SurfaceForms / resolution.Entities.Count;
        metrics.RelatedToShare = result.Relations.Count == 0
            ? 0
            : (double)result.Relations.Count(r => r.Predicate == Ontology.Fallback) / result.Relations.Count;

        var connected = result.Relations.SelectMany(r => new[] { r.SubjectKey, r.ObjectKey }).ToHashSet(StringComparer.Ordinal);
        metrics.OrphanEntityRate = resolution.Entities.Count == 0
            ? 0
            : (double)resolution.Entities.Count(e => !connected.Contains(e.Key)) / resolution.Entities.Count;

        metrics.PersonCharacterCollisionBlocks = resolution.CrossTypeBlocks;
        metrics.CrossTypeNameCollisions = resolution.CrossTypeNameCollisions;
        metrics.TriplesPerChunk = chunkCount == 0 ? 0 : (double)result.Relations.Count / chunkCount;

        if (metrics.RelatedToShare > 0.20)
            result.Warnings.Add($"RELATED_TO share is {metrics.RelatedToShare:P0} — above 20% means the ontology is not being applied. Check the extraction prompt.");
        if (metrics.DirectionFlipRate > 0.15)
            result.Warnings.Add($"Direction flip rate is {metrics.DirectionFlipRate:P0} — above 15% means the prompt's direction table needs work.");
        if (metrics.GroundingPassRate < 0.70)
            result.Warnings.Add($"Grounding pass rate is {metrics.GroundingPassRate:P0}, below the 0.70 healthy floor. The model is inventing evidence spans.");
    }

    private static string Tail(string text, int chars) =>
        text.Length <= chars ? text : text[^chars..];
}
