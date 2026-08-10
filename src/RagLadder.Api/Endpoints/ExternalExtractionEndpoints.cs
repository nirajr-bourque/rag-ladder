using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RagLadder.Api.Configuration;
using RagLadder.Api.Extraction;
using RagLadder.Api.Infrastructure;
using RagLadder.Api.Models;

namespace RagLadder.Api.Endpoints;

/// <summary>
/// Bring your own model.
///
/// A small local model can fail the extraction task outright — inventing predicates, paraphrasing
/// evidence, omitting the entities its own relations point at — and no amount of filter tuning
/// fixes a capability gap. This exports the exact prompt and chunks so they can be run through a
/// capable model elsewhere, then imports the result into the extraction cache.
///
/// Only the model call is externalised. Everything downstream is untouched: the same seven
/// filters, the same entity resolution, the same funnel, the same review gate. A triple pasted in
/// from outside still has to survive grounding and ontology conformance like any other, so the
/// honesty of the demo is preserved.
/// </summary>
public static class ExternalExtractionEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public sealed class ImportPayload
    {
        public List<ImportedChunk> Chunks { get; set; } = [];
    }

    public sealed class ImportedChunk
    {
        public string ChunkId { get; set; } = "";
        public List<RawEntity> Entities { get; set; } = [];
        public List<RawRelation> Relations { get; set; } = [];
    }

    public static void MapExternalExtractionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/documents/{id}/extraction").WithTags("external-extraction");

        // ----- export ------------------------------------------------------

        group.MapGet("/prompt", (
            string id,
            [FromQuery] int? take,
            [FromQuery] bool? json,
            CorpusRepository corpus,
            ExtractionService extraction,
            IOptions<RagLadderOptions> options) =>
        {
            var (chunks, sections, byOrdinal) = Load(id, corpus, options);
            if (chunks.Count == 0)
                return Results.NotFound(new { error = "No chunks for this document. Process it first." });

            var selected = chunks.Take(Math.Clamp(take ?? chunks.Count, 1, chunks.Count)).ToList();

            if (json == true)
            {
                return Results.Ok(new
                {
                    system = extraction.SystemPrompt(),
                    chunks = selected.Select(c => new
                    {
                        chunkId = c.Id,
                        user = extraction.UserPromptFor(c, sections.GetValueOrDefault(c.SectionId), byOrdinal),
                    }),
                });
            }

            return Results.Text(BuildPasteable(extraction, selected, sections, byOrdinal),
                "text/markdown", Encoding.UTF8);
        });

        // ----- import ------------------------------------------------------

        group.MapPost("/import", (
            string id,
            [FromBody] ImportPayload payload,
            CorpusRepository corpus,
            ExtractionService extraction,
            IOptions<RagLadderOptions> options) =>
        {
            var (chunks, _, byOrdinal) = Load(id, corpus, options);
            if (chunks.Count == 0)
                return Results.NotFound(new { error = "No chunks for this document. Process it first." });

            var byId = chunks.ToDictionary(c => c.Id, c => c, StringComparer.Ordinal);
            var imported = 0;
            var unknown = new List<string>();
            var entities = 0;
            var relations = 0;

            foreach (var incoming in payload.Chunks)
            {
                if (!byId.TryGetValue(incoming.ChunkId, out var chunk))
                {
                    unknown.Add(incoming.ChunkId);
                    continue;
                }

                extraction.SeedCache(extraction.CacheKeyFor(chunk, byOrdinal), new RawExtraction
                {
                    Entities = incoming.Entities,
                    Relations = incoming.Relations,
                });
                imported++;
                entities += incoming.Entities.Count;
                relations += incoming.Relations.Count;
            }

            return Results.Ok(new
            {
                imported,
                totalChunks = chunks.Count,
                entities,
                relations,
                unknownChunkIds = unknown,
                next = "Run Process again. Every imported chunk is now a cache hit, so no model " +
                       "calls are made and the usual filters, funnel and review gate all apply.",
            });
        });
    }

    private static (List<ChunkRecord> Chunks, Dictionary<string, SectionRecord> Sections, Dictionary<int, ChunkRecord> ByOrdinal)
        Load(string id, CorpusRepository corpus, IOptions<RagLadderOptions> options)
    {
        var strategy = options.Value.Extraction.SourceStrategy;
        var chunks = corpus.GetChunks(id, strategy).OrderBy(c => c.StrategyOrdinal).ToList();
        var sections = corpus.GetSections(id).ToDictionary(s => s.Id, s => s, StringComparer.Ordinal);
        var byOrdinal = chunks.ToDictionary(c => c.StrategyOrdinal, c => c);
        return (chunks, sections, byOrdinal);
    }

    /// <summary>
    /// One self-contained document to paste into a chat with a capable model. The instructions ask
    /// for a single JSON object keyed by chunk id, which is exactly the import shape.
    /// </summary>
    private static string BuildPasteable(
        ExtractionService extraction,
        IReadOnlyList<ChunkRecord> chunks,
        IReadOnlyDictionary<string, SectionRecord> sections,
        IReadOnlyDictionary<int, ChunkRecord> byOrdinal)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Knowledge-graph extraction request");
        sb.AppendLine();
        sb.AppendLine("Paste this whole document into a chat with a capable model. It will reply with one");
        sb.AppendLine("JSON object. Save that reply to a file and import it:");
        sb.AppendLine();
        sb.AppendLine("```powershell");
        sb.AppendLine("pwsh tools/import-extraction.ps1 -File response.json");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Instructions");
        sb.AppendLine();
        sb.AppendLine(extraction.SystemPrompt());
        sb.AppendLine();
        sb.AppendLine("## Output shape");
        sb.AppendLine();
        sb.AppendLine("Return ONE JSON object covering every chunk below, and nothing else:");
        sb.AppendLine();
        sb.AppendLine("""
            {
              "chunks": [
                {
                  "chunkId": "doc_abc#12",
                  "entities": [ { "name": "...", "type": "Person", "evidence": "..." } ],
                  "relations": [ { "subject": "...", "predicate": "ACTED_IN", "object": "...",
                                   "evidence": "...", "confidence": 0.9, "properties": {} } ]
                }
              ]
            }
            """);
        sb.AppendLine();
        sb.AppendLine("Include every chunkId exactly as given, even if a chunk yields nothing —");
        sb.AppendLine("in that case return empty arrays for it.");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"## Chunks ({chunks.Count})");

        foreach (var chunk in chunks)
        {
            sb.AppendLine();
            sb.AppendLine($"### chunkId: `{chunk.Id}`");
            sb.AppendLine();
            sb.AppendLine(extraction.UserPromptFor(chunk, sections.GetValueOrDefault(chunk.SectionId), byOrdinal));
        }

        return sb.ToString();
    }
}
