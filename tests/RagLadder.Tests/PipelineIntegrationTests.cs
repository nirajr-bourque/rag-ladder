using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RagLadder.Api.Llm;
using Xunit;

namespace RagLadder.Tests;

/// <summary>
/// Boots the real application against the local providers and a scripted model, then walks the
/// pipeline end to end: load the demo PDF, process it, pass the review gate, commit the graph,
/// and ask across the ladder. Nothing here touches the network.
/// </summary>
public sealed class PipelineFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public string DataDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "ragladder-tests", Guid.NewGuid().ToString("N")[..8]);

    public string? DocumentId { get; private set; }
    public ScriptedChatClient Chat { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("RagLadder:Storage:DataDirectory", DataDirectory);
        builder.UseSetting("RagLadder:Providers:Vector", "memory");
        builder.UseSetting("RagLadder:Providers:Graph", "memory");
        builder.UseSetting("RagLadder:Providers:Embedder", "hash");
        builder.UseSetting("RagLadder:Providers:Reranker", "lexical");
        builder.UseSetting("RagLadder:Ollama:ValidateTagsAtStartup", "false");

        // Pin the corpus. These tests assert facts specific to the full dossier — 92 sections,
        // Sivalingam's split filmography, the two Fantastic Four years — so they must not follow
        // the app's default, which is the smaller Spider-Man seed.
        builder.UseSetting("RagLadder:Storage:DemoPdf", "serendib-dossier.pdf");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IChatClient>();
            services.AddSingleton<IChatClient>(Chat);
        });
    }

    public async Task InitializeAsync()
    {
        var client = CreateClient();

        var pdf = Path.Combine(FindRepoRoot(), "corpus", "demo", "serendib-dossier.pdf");
        Assert.True(File.Exists(pdf),
            $"Demo PDF missing at {pdf}. Build it with: dotnet run --project tools/RagLadder.CorpusBuilder");

        var load = await client.PostAsync("/api/documents/load-demo", null);
        load.EnsureSuccessStatusCode();
        var document = await load.Content.ReadFromJsonAsync<JsonNode>();
        DocumentId = document!["id"]!.GetValue<string>();

        var process = await client.PostAsJsonAsync($"/api/documents/{DocumentId}/process", new
        {
            mode = "thorough",
            skipReview = false,
            chunkCap = 40,
            spreadSampling = false,
        });
        process.EnsureSuccessStatusCode();

        // Wait for the pipeline to reach the review gate.
        for (var attempt = 0; attempt < 240; attempt++)
        {
            var status = await client.GetFromJsonAsync<JsonNode>($"/api/documents/{DocumentId}/status");
            var job = status?["job"];
            if (job is not null)
            {
                if (job["failed"]!.GetValue<bool>())
                    throw new InvalidOperationException("Processing failed: " + job["message"]);
                if (job["awaitingReview"]!.GetValue<bool>() || job["completed"]!.GetValue<bool>()) break;
            }
            await Task.Delay(250);
        }
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        try { if (Directory.Exists(DataDirectory)) Directory.Delete(DataDirectory, true); }
        catch (IOException) { /* the SQLite file may still be held briefly on Windows */ }
    }

    internal static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RagLadder.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? Directory.GetCurrentDirectory();
    }
}

[CollectionDefinition("pipeline")]
public sealed class PipelineCollection : ICollectionFixture<PipelineFixture>;

[Collection("pipeline")]
public sealed class PipelineIntegrationTests(PipelineFixture fixture)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private HttpClient Client => fixture.CreateClient();
    private string Doc => fixture.DocumentId!;

    // ----- phase 2 acceptance: parsing and front matter ---------------------

    [Fact]
    public async Task Front_matter_parses_to_the_right_docType_subject_and_year()
    {
        var detail = await Client.GetFromJsonAsync<JsonNode>($"/api/documents/{Doc}");
        var sections = detail!["sections"]!.AsArray();

        Assert.True(sections.Count > 40, $"Expected the dossier to segment into many sections, got {sections.Count}.");

        var withFrontMatter = sections.Count(s => s!["frontMatter"]?["docType"] is not null);
        Assert.True(withFrontMatter > 30,
            $"Only {withFrontMatter} sections carry a parsed front matter block; the header parser is not working.");

        var ironMan = sections.FirstOrDefault(s =>
            s!["frontMatter"]?["subject"]?.GetValue<string>() == "Iron Man" &&
            s["frontMatter"]?["year"]?.GetValue<int>() == 2008);
        Assert.NotNull(ironMan);
        Assert.Equal("title-record", ironMan!["frontMatter"]!["docType"]!.GetValue<string>());
    }

    /// <summary>
    /// Both appendices must be absent from the index. Appendix B is the answer key mapping every
    /// invented name to its real-world counterpart; Appendix A is the trap map, and it writes out
    /// the stage-10 connection path in plain text. Either one, ingested, hands the retriever the
    /// answers the ladder is supposed to earn.
    /// </summary>
    [Fact]
    public async Task Both_appendices_were_stripped_before_ingestion()
    {
        var chunks = await Client.GetFromJsonAsync<JsonNode>($"/api/documents/{Doc}/chunks?strategy=recursive&take=500");
        var text = string.Concat(chunks!["chunks"]!.AsArray().Select(c => c!["rawText"]!.GetValue<string>()));

        // Appendix B — the answer key.
        Assert.DoesNotContain("Corpus equivalent", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Tony Stark performer", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Real-world entity", text, StringComparison.OrdinalIgnoreCase);

        // Appendix A — the trap map, including the spelled-out connection path.
        Assert.DoesNotContain("Question that breaks", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Trap 10, spelled out", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Split filmography", text, StringComparison.OrdinalIgnoreCase);

        // The corpus body itself must survive intact.
        Assert.Contains("Sinharaja Studios", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Arjun Sivalingam", text, StringComparison.OrdinalIgnoreCase);
    }

    // ----- phase 3 acceptance: three collections ----------------------------

    [Fact]
    public async Task All_three_chunking_strategies_are_indexed()
    {
        var detail = await Client.GetFromJsonAsync<JsonNode>($"/api/documents/{Doc}");
        var counts = detail!["chunkCounts"]!.AsObject();

        foreach (var strategy in new[] { "fixed", "recursive", "contextual" })
            Assert.True(counts[strategy]!.GetValue<int>() > 20, $"Strategy '{strategy}' produced too few chunks.");

        // Overlap means recursive produces at least as many chunks as fixed over the same text.
        Assert.True(counts["recursive"]!.GetValue<int>() >= counts["fixed"]!.GetValue<int>());
    }

    [Fact]
    public async Task Contextual_chunks_carry_the_prefix_that_names_the_work()
    {
        var chunks = await Client.GetFromJsonAsync<JsonNode>($"/api/documents/{Doc}/chunks?strategy=contextual&take=30");
        var first = chunks!["chunks"]!.AsArray().First()!;

        var indexed = first["text"]!.GetValue<string>();
        var raw = first["rawText"]!.GetValue<string>();
        Assert.NotEqual(indexed, raw);
        Assert.EndsWith(raw, indexed);
    }

    // ----- the traps are the demo, so they get tests --------------------------

    /// <summary>
    /// Trap 1. Arjun Sivalingam's chronological credits straddle a forced page boundary. The
    /// structure-blind fixed strategy can only ever see half of them; the recursive strategy,
    /// which chunks per section, sees the whole list. If this stops holding, stage 1 and stage 2
    /// answer identically and the rung teaches nothing.
    /// </summary>
    [Fact]
    public async Task Trap_one_splits_the_filmography_under_fixed_chunking_and_not_under_recursive()
    {
        var client = Client;
        var fixedChunks = await Chunks(client, "fixed");
        var recursiveChunks = await Chunks(client, "recursive");

        const string early = "Iron Man 2";
        const string late = "The Marvels";
        static bool Mentions(string text, string title) =>
            text.Contains(title, StringComparison.OrdinalIgnoreCase) &&
            text.Contains("Nick Fury", StringComparison.OrdinalIgnoreCase);

        var fixedWithBoth = fixedChunks.Count(c => Mentions(c, early) && Mentions(c, late));
        Assert.Equal(0, fixedWithBoth);

        var fixedWithEither = fixedChunks.Count(c => Mentions(c, early) || Mentions(c, late));
        Assert.True(fixedWithEither >= 2, "The filmography should appear, split, across at least two fixed chunks.");

        var recursiveWithBoth = recursiveChunks.Count(c => Mentions(c, early) && Mentions(c, late));
        Assert.True(recursiveWithBoth >= 1,
            "Section-scoped recursive chunking must keep the whole filmography in one chunk — that is what stage 2 fixes.");
    }

    /// <summary>
    /// Trap 11. Two unrelated films share the title Fantastic Four. Their sections must carry
    /// different years, which is what lets the stage-3 year filter separate the 2005 and 2015 casts.
    /// </summary>
    [Fact]
    public async Task Trap_eleven_keeps_the_two_films_sharing_a_title_apart_by_year()
    {
        var detail = await Client.GetFromJsonAsync<JsonNode>($"/api/documents/{Doc}");

        // Numbered title records only: part dividers inherit the previous block's metadata.
        var fantasticFour = detail!["sections"]!.AsArray()
            .Where(s => s!["frontMatter"]?["subject"]?.GetValue<string>() == "Fantastic Four")
            .Where(s => s!["heading"]!.GetValue<string>().StartsWith("Section ", StringComparison.Ordinal))
            .Select(s => s!["frontMatter"]!["year"]!.GetValue<int>())
            .OrderBy(y => y)
            .ToList();

        Assert.Equal([2005, 2015], fantasticFour);
    }

    /// <summary>
    /// Trap 6. The episode-record chunk says "She confronts the neighbour" and names neither the
    /// series nor the character. Only the contextual prefix supplies the missing referent.
    /// </summary>
    [Fact]
    public async Task Trap_six_leaves_the_orphan_chunk_without_a_referent_until_the_contextual_prefix()
    {
        var client = Client;
        var recursive = await Chunks(client, "recursive");
        var orphan = recursive.FirstOrDefault(c => c.Contains("confronts the neighbour", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(orphan);
        Assert.DoesNotContain("WandaVision", orphan.Split("Episode Record")[^1], StringComparison.OrdinalIgnoreCase);

        var contextualResponse = await client.GetFromJsonAsync<JsonNode>(
            $"/api/documents/{Doc}/chunks?strategy=contextual&take=500");
        var contextual = contextualResponse!["chunks"]!.AsArray()
            .First(c => c!["text"]!.GetValue<string>().Contains("confronts the neighbour", StringComparison.OrdinalIgnoreCase));

        Assert.Contains("WandaVision", contextual!["text"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<List<string>> Chunks(HttpClient client, string strategy)
    {
        var response = await client.GetFromJsonAsync<JsonNode>(
            $"/api/documents/{Doc}/chunks?strategy={strategy}&take=500");
        return [.. response!["chunks"]!.AsArray().Select(c => c!["rawText"]!.GetValue<string>())];
    }

    /// <summary>A warm-cache reprocess must make zero embedder calls (spec §11 phase 3).</summary>
    [Fact]
    public async Task Reprocessing_an_unchanged_document_makes_no_embedder_calls()
    {
        var client = Client;
        var response = await client.PostAsJsonAsync($"/api/documents/{Doc}/process",
            new { mode = "quick", skipReview = true, skipExtraction = true });
        response.EnsureSuccessStatusCode();

        JsonNode? job = null;
        for (var attempt = 0; attempt < 240; attempt++)
        {
            var status = await client.GetFromJsonAsync<JsonNode>($"/api/documents/{Doc}/status");
            job = status?["job"];
            if (job?["completed"]?.GetValue<bool>() == true || job?["failed"]?.GetValue<bool>() == true) break;
            await Task.Delay(250);
        }

        Assert.NotNull(job);
        Assert.False(job!["failed"]!.GetValue<bool>(), job["message"]?.GetValue<string>());
        var warnings = job["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).ToList();
        Assert.Contains(warnings, w => w.Contains("Warm cache: zero embedder calls", StringComparison.Ordinal));
    }

    // ----- phase 6 acceptance: extraction and the review gate ---------------

    [Fact]
    public async Task The_review_gate_holds_a_proposed_graph_with_a_funnel()
    {
        var extraction = await Client.GetFromJsonAsync<JsonNode>($"/api/documents/{Doc}/extraction");
        var funnel = extraction!["funnel"]!;

        Assert.True(funnel["extracted"]!.GetValue<int>() > 0, "Nothing was extracted at all.");
        Assert.True(funnel["grounded"]!.GetValue<int>() > 0, "Nothing survived the grounding filter.");
        Assert.True(extraction["relations"]!.AsArray().Count > 0);
        Assert.True(extraction["entities"]!.AsArray().Count > 0);
    }

    [Fact]
    public async Task Every_committed_triple_carries_a_verbatim_evidence_span()
    {
        var extraction = await Client.GetFromJsonAsync<JsonNode>($"/api/documents/{Doc}/extraction");
        var chunks = await Client.GetFromJsonAsync<JsonNode>($"/api/documents/{Doc}/chunks?strategy=recursive&take=500");
        var byId = chunks!["chunks"]!.AsArray()
            .ToDictionary(c => c!["id"]!.GetValue<string>(), c => c!["rawText"]!.GetValue<string>());

        var checkedCount = 0;
        foreach (var relation in extraction!["relations"]!.AsArray().Take(80))
        {
            var evidence = relation!["evidence"]!.GetValue<string>();
            var chunkId = relation["chunkIds"]!.AsArray().FirstOrDefault()?.GetValue<string>();
            if (chunkId is null || !byId.TryGetValue(chunkId, out var text)) continue;

            checkedCount++;
            Assert.True(
                RagLadder.Api.Extraction.ExtractionFilters.IsGrounded(evidence, text),
                $"Evidence is not a verbatim span of its chunk: \"{evidence}\"");
        }
        Assert.True(checkedCount > 5, "Too few triples were checkable against their source chunk.");
    }

    /// <summary>The type barrier is absolute: a performer and a role never become one node.</summary>
    [Fact]
    public async Task No_person_is_ever_merged_with_a_character()
    {
        var extraction = await Client.GetFromJsonAsync<JsonNode>($"/api/documents/{Doc}/extraction");
        foreach (var entity in extraction!["entities"]!.AsArray())
        {
            var key = entity!["key"]!.GetValue<string>();
            var type = entity["type"]!.GetValue<string>();
            var expectedPrefix = type switch
            {
                "Person" => "person:",
                "Character" => "character:",
                "Film" => "film:",
                "Studio" => "studio:",
                _ => null
            };
            if (expectedPrefix is not null) Assert.StartsWith(expectedPrefix, key);
        }
    }

    [Fact]
    public async Task Inverted_crew_credits_are_flipped_rather_than_dropped()
    {
        var extraction = await Client.GetFromJsonAsync<JsonNode>($"/api/documents/{Doc}/extraction");
        var shotBy = extraction!["relations"]!.AsArray()
            .Where(r => r!["predicate"]!.GetValue<string>() == "SHOT_BY")
            .ToList();

        Assert.NotEmpty(shotBy);
        foreach (var relation in shotBy)
        {
            // SHOT_BY runs work -> person, whichever way the model emitted it.
            Assert.Equal("Person", relation!["objectType"]!.GetValue<string>());
            Assert.True(relation["flipped"]!.GetValue<bool>(),
                "The scripted client emits SHOT_BY backwards on purpose; the direction filter should have flipped it.");
        }
    }

    // ----- phase 7 acceptance: commit, traversal, aggregation ---------------

    [Fact]
    public async Task Committing_writes_the_graph_and_derives_collaboration_edges()
    {
        var client = Client;
        var commit = await client.PostAsync($"/api/documents/{Doc}/graph/commit", null);
        commit.EnsureSuccessStatusCode();
        var summary = await commit.Content.ReadFromJsonAsync<JsonNode>();

        Assert.True(summary!["nodes"]!.GetValue<int>() > 0);
        Assert.True(summary["edges"]!.GetValue<int>() > 0);

        var snapshot = await client.GetFromJsonAsync<JsonNode>($"/api/documents/{Doc}/graph?limit=2000");
        Assert.True(snapshot!["nodes"]!.AsArray().Count > 0);

        var derived = snapshot["edges"]!.AsArray().Where(e => e!["derived"]!.GetValue<bool>()).ToList();
        Assert.True(derived.Count > 0, "COLLABORATED_WITH edges should be derived after commit.");
        Assert.All(derived, e => Assert.Equal("COLLABORATED_WITH", e!["predicate"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Shortest_path_connects_two_people_who_never_shared_a_title()
    {
        var client = Client;
        await client.PostAsync($"/api/documents/{Doc}/graph/commit", null);

        var people = await client.GetFromJsonAsync<JsonNode>($"/api/documents/{Doc}/graph/entities?type=Person&limit=500");
        var keys = people!.AsArray().Select(p => p!["key"]!.GetValue<string>()).ToList();
        Assert.True(keys.Count > 5, "Not enough Person nodes to test traversal.");

        // Try pairs until one is connected; the corpus guarantees a well-linked core.
        JsonNode? found = null;
        foreach (var from in keys.Take(12))
        {
            foreach (var to in keys.Take(12).Where(k => k != from))
            {
                var response = await client.GetFromJsonAsync<JsonNode>(
                    $"/api/documents/{Doc}/graph/path?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}&maxHops=8");
                if (response!["found"]!.GetValue<bool>()) { found = response["path"]; break; }
            }
            if (found is not null) break;
        }

        Assert.NotNull(found);
        Assert.True(found!["hops"]!.GetValue<int>() >= 1);
        Assert.True(found["nodes"]!.AsArray().Count >= 2);
        Assert.False(string.IsNullOrWhiteSpace(found["narrative"]!.GetValue<string>()),
            "The path must render as prose built from the traversal.");
    }

    /// <summary>
    /// Chunk provenance has to survive the review gate's JSON round-trip through SQLite. If it
    /// does not, the graph commits with no MENTIONS edges and the stage-10 entity hop silently
    /// returns nothing while still looking like it worked.
    /// </summary>
    [Fact]
    public async Task Stage_ten_expansion_reaches_entities_through_chunk_provenance()
    {
        var client = Client;
        await client.PostAsync($"/api/documents/{Doc}/graph/commit", null);

        var response = await client.PostAsJsonAsync("/api/ask", new
        {
            documentId = Doc,
            question = "Who is credited on Iron Man?",
            options = new
            {
                collection = "recursive",
                topK = 5,
                useGraphExpansion = true,
                graphMode = "expand",
                graphHops = new { next = true, parent = true, entity = true, entityRel = true },
                minEdgeConfidence = 0.0,
            },
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonNode>();

        var graph = body!["graph"]!;
        Assert.Equal("expand", graph["mode"]!.GetValue<string>());
        Assert.NotEmpty(graph["seedChunkIds"]!.AsArray());
        Assert.True(graph["entitiesTouched"]!.AsArray().Count > 0,
            "The entity hop found nothing — chunk provenance was lost between extraction and commit.");
    }

    [Fact]
    public async Task Aggregation_runs_as_pure_graph_work_and_exposes_its_cypher()
    {
        var client = Client;
        await client.PostAsync($"/api/documents/{Doc}/graph/commit", null);

        var result = await client.GetFromJsonAsync<JsonNode>(
            $"/api/documents/{Doc}/graph/aggregate?preset=director-cinematographer-pairs&minConfidence=0");

        Assert.Equal("director-cinematographer-pairs", result!["presetId"]!.GetValue<string>());
        Assert.Contains("MATCH", result["cypher"]!.GetValue<string>());
        Assert.NotEmpty(result["columns"]!.AsArray());
    }

    [Fact]
    public async Task An_unknown_aggregation_preset_is_rejected_with_the_available_list()
    {
        var response = await Client.GetAsync($"/api/documents/{Doc}/graph/aggregate?preset=nonsense");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonNode>();
        Assert.NotNull(body!["available"]);
    }

    // ----- the ladder --------------------------------------------------------

    [Fact]
    public async Task Stage_zero_is_unconstrained_and_flagged_as_such()
    {
        var response = await Ask(0, "Who directed Iron Man (2008)?");
        Assert.True(response!["unconstrained"]!.GetValue<bool>());
        Assert.Null(response["retrieval"]);
        Assert.Contains(response["warnings"]!.AsArray().Select(w => w!.GetValue<string>()),
            w => w.Contains("unconstrained", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Stage_one_retrieves_from_the_fixed_collection_and_stage_two_from_recursive()
    {
        var one = await Ask(1, "Who directed Iron Man (2008)?");
        var two = await Ask(2, "Who directed Iron Man (2008)?");

        Assert.Equal("fixed", one!["retrieval"]!["collection"]!.GetValue<string>());
        Assert.Equal("recursive", two!["retrieval"]!["collection"]!.GetValue<string>());
        Assert.NotEmpty(one["retrieval"]!["chunks"]!.AsArray());
    }

    [Fact]
    public async Task Hybrid_search_labels_the_arm_that_found_each_candidate()
    {
        var response = await Ask(4, "What was the domestic opening weekend of 3,571,150,070?");
        var candidates = response!["retrieval"]!["candidates"]!.AsArray();

        Assert.True(response["retrieval"]!["hybrid"]!.GetValue<bool>());
        var arms = candidates.Select(c => c!["arm"]!.GetValue<string>()).Distinct().ToList();
        Assert.Contains(arms, a => a is "keyword" or "both");
    }

    /// <summary>
    /// The keyword arm is the reason hybrid search earns its rung: exact figures are what
    /// embeddings are worst at. These tests run on the deterministic dev embedder, which is itself
    /// lexical, so the arm labels can legitimately read "both" here — with the real ONNX embedder
    /// this hit is keyword-only. What must hold either way is that the keyword arm ranks the
    /// figure's own chunk first.
    /// </summary>
    [Fact]
    public async Task The_keyword_arm_ranks_the_chunk_holding_an_exact_figure_first()
    {
        var response = await Ask(4, "3,571,150,070");
        var retrieval = response!["retrieval"]!;
        var candidates = retrieval["candidates"]!.AsArray();

        Assert.True(retrieval["hybrid"]!.GetValue<bool>());
        Assert.NotEmpty(candidates);

        var arms = candidates.Select(c => c!["arm"]!.GetValue<string>()).ToList();
        Assert.Contains(arms, a => a is "keyword" or "both");

        var top = retrieval["chunks"]!.AsArray().First()!;
        Assert.Contains("3,571,150,070", top["text"]!.GetValue<string>());
        Assert.NotNull(top["keywordScore"]);
    }

    [Fact]
    public async Task Reranking_reports_rank_before_and_after()
    {
        // Deliberately phrased without a title, so the stage-3 metadata filter does not narrow the
        // candidate pool and the rerank rank deltas stay visible.
        var response = await Ask(5, "Which crew member is credited with the original score?");
        var retrieval = response!["retrieval"]!;

        Assert.True(retrieval["reranked"]!.GetValue<bool>());
        Assert.True(retrieval["candidateCount"]!.GetValue<int>() > retrieval["chunks"]!.AsArray().Count);
        Assert.True(retrieval["droppedCount"]!.GetValue<int>() > 0);

        var candidate = retrieval["candidates"]!.AsArray().First()!;
        Assert.NotNull(candidate["rankBefore"]);
        Assert.NotNull(candidate["rankAfter"]);
    }

    [Fact]
    public async Task Stage_six_records_the_rewritten_query()
    {
        var response = await Ask(6, "Who did the music for Black Panther?");
        Assert.Equal("REWRITTEN", response!["rewrite"]!["rewritten"]!.GetValue<string>());
    }

    [Fact]
    public async Task Stage_seven_uses_the_contextual_collection()
    {
        var response = await Ask(7, "Which episode has the confrontation with the neighbour?");
        Assert.Equal("contextual", response!["retrieval"]!["collection"]!.GetValue<string>());
    }

    [Fact]
    public async Task Stage_eleven_records_its_classification_and_route()
    {
        var response = await Ask(11, "Who directed Black Panther?");
        Assert.Equal("lookup", response!["router"]!["classification"]!.GetValue<string>());
        Assert.Equal("vector-only", response["router"]!["route"]!.GetValue<string>());
    }

    /// <summary>Two rungs must never share a cached answer (spec §7.4).</summary>
    [Fact]
    public async Task No_two_stages_share_a_cached_answer()
    {
        const string question = "Who composed the score for Iron Man (2008)?";
        var first = await Ask(2, question);
        var second = await Ask(4, question);
        var repeat = await Ask(2, question);

        Assert.False(first!["fromCache"]!.GetValue<bool>());
        Assert.False(second!["fromCache"]!.GetValue<bool>());
        Assert.True(repeat!["fromCache"]!.GetValue<bool>(),
            "Re-asking the same question at the same stage should hit the answer cache.");
        Assert.NotEqual(
            first["retrieval"]!["collection"]!.GetValue<string>() + first["options"]!["useHybrid"]!.GetValue<bool>(),
            second["retrieval"]!["collection"]!.GetValue<string>() + second["options"]!["useHybrid"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Compare_runs_each_rung_independently()
    {
        var response = await Client.PostAsJsonAsync("/api/compare", new
        {
            documentId = Doc,
            question = "How many features has Arjun Sivalingam appeared in as Nick Fury?",
            stages = new[] { 1, 2 },
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonNode>();

        var results = body!["results"]!.AsArray();
        Assert.Equal(2, results.Count);
        Assert.Equal("fixed", results[0]!["retrieval"]!["collection"]!.GetValue<string>());
        Assert.Equal("recursive", results[1]!["retrieval"]!["collection"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_out_of_range_stage_is_rejected()
    {
        var response = await Client.PostAsJsonAsync("/api/ask/stage/12",
            new { documentId = Doc, question = "anything" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Health_reports_the_provider_posture()
    {
        var health = await Client.GetFromJsonAsync<JsonNode>("/api/health");
        var providers = health!["providers"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()).ToList();

        Assert.Contains("embedder", providers);
        Assert.Contains("vector", providers);
        Assert.Contains("graph", providers);
        Assert.Contains("chat", providers);
        Assert.NotNull(health["embedder"]!["similarPair"]);
    }

    [Fact]
    public async Task The_golden_set_loads_with_a_control_group()
    {
        var client = Client;
        var load = await client.PostAsync($"/api/documents/{Doc}/golden/load", null);
        load.EnsureSuccessStatusCode();

        var golden = await client.GetFromJsonAsync<JsonNode>($"/api/documents/{Doc}/golden");
        var questions = golden!["questions"]!.AsArray();

        Assert.True(questions.Count >= 52, $"Expected at least 52 golden questions, found {questions.Count}.");
        var ungrounded = questions.Where(q => q!["type"]!.GetValue<string>() == "ungrounded").ToList();
        Assert.Equal(4, ungrounded.Count);
        Assert.All(ungrounded, q => Assert.True(q!["expectRefusal"]!.GetValue<bool>()));

        // Four per type, thirteen types: the spec's eleven, plus ungrounded, plus name_collision
        // for trap 12 as the corpus appendix defines it.
        var byType = questions.GroupBy(q => q!["type"]!.GetValue<string>()).ToList();
        Assert.Equal(13, byType.Count);
        Assert.All(byType, g => Assert.Equal(4, g.Count()));

        // Every trap in the corpus appendix is exercised by at least one question.
        var traps = questions.Select(q => q!["trap"]?.GetValue<int>()).Where(t => t is not null).Distinct().ToList();
        foreach (var trap in new[] { 1, 2, 3, 5, 7, 8, 9, 10, 11, 12 })
            Assert.Contains(trap, traps);
    }

    private async Task<JsonNode?> Ask(int stage, string question)
    {
        var response = await Client.PostAsJsonAsync($"/api/ask/stage/{stage}",
            new { documentId = Doc, question });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonNode>(Json);
    }
}

internal static class ServiceCollectionTestExtensions
{
    public static void RemoveAll<T>(this IServiceCollection services)
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(T)).ToList())
            services.Remove(descriptor);
    }
}
