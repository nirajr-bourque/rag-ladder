using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using RagLadder.Api.Ask;
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
using Xunit;

namespace RagLadder.Tests;

public class ChunkingTests
{
    private static readonly ChunkingOptions Options = new();

    [Fact]
    public void Fixed_chunker_produces_no_overlap()
    {
        var text = string.Join(' ', Enumerable.Range(0, 900).Select(i => "word" + i));
        var spans = new FixedChunker(Options).Split(text, 0);

        Assert.True(spans.Count > 1);
        for (var i = 1; i < spans.Count; i++)
            Assert.Equal(spans[i - 1].End, spans[i].Start);
    }

    [Fact]
    public void Recursive_chunker_overlaps_consecutive_chunks()
    {
        var paragraphs = Enumerable.Range(0, 40)
            .Select(i => $"Paragraph {i}. " + string.Join(' ', Enumerable.Range(0, 25).Select(j => $"token{i}x{j}")));
        var text = string.Join("\n\n", paragraphs);

        var spans = new RecursiveChunker(Options).Split(text, 0);

        Assert.True(spans.Count > 2);
        var overlaps = 0;
        for (var i = 1; i < spans.Count; i++)
            if (spans[i].Start < spans[i - 1].End) overlaps++;
        Assert.True(overlaps > 0, "Recursive chunking must carry overlap forward; that overlap is what fixes trap 1.");
    }

    [Fact]
    public void Chunk_spans_map_back_to_the_source_text()
    {
        var text = string.Join("\n", Enumerable.Range(0, 200).Select(i => $"line {i} of the section body"));
        foreach (var span in new RecursiveChunker(Options).Split(text, 1000))
        {
            Assert.True(span.Start >= 1000);
            Assert.Equal(span.Text, text[(span.Start - 1000)..(span.End - 1000)]);
        }
    }

    [Fact]
    public void Contextual_prefix_names_the_work_and_the_year()
    {
        var prefix = ContextualPrefix.Build(
            new FrontMatter { Subject = "WandaVision", Year = 2021, DocType = "episode-record" },
            "Episode records for the series.");

        Assert.Contains("WandaVision", prefix);
        Assert.Contains("2021", prefix);
        Assert.Contains("episode-record", prefix);
    }
}

public class FrontMatterTests
{
    [Fact]
    public void Parses_the_header_block_keys()
    {
        var fm = FrontMatterParser.Parse([
            "docType: title-record",
            "subject: Iron Man",
            "year: 2008",
            "studio: Sinharaja Studios",
            "market: domestic"
        ]);

        Assert.Equal("title-record", fm.DocType);
        Assert.Equal("Iron Man", fm.Subject);
        Assert.Equal(2008, fm.Year);
        Assert.Equal("Sinharaja Studios", fm.Studio);
        Assert.Equal("domestic", fm.Market);
    }

    [Fact]
    public void Treats_null_as_absent()
    {
        var fm = FrontMatterParser.Parse(["market: null", "studio: null"]);
        Assert.Null(fm.Market);
        Assert.Null(fm.Studio);
    }

    [Fact]
    public void Ignores_prose_lines()
    {
        Assert.False(FrontMatterParser.IsFrontMatterLine("Release 2 May 2008 - 127 min"));
        Assert.True(FrontMatterParser.IsFrontMatterLine("  docType: press-kit  "));
    }
}

public class RetrievalScoringTests
{
    [Fact]
    public void Bm25_keeps_currency_figures_as_single_tokens()
    {
        var tokens = Bm25.Tokenize("Domestic opening weekend LKR 3,571,150,070 and $47.3M besides.");
        Assert.Contains("3,571,150,070", tokens);
        Assert.Contains("$47.3m", tokens);
    }

    [Fact]
    public void Bm25_finds_the_exact_figure_that_embeddings_would_miss()
    {
        var documents = new List<(string, string)>
        {
            ("a", "The film was well received by audiences across the region."),
            ("b", "Domestic opening weekend 3,571,150,070 across 4,664 screens."),
            ("c", "Production notes describe an extended shoot in Kandy."),
        };
        var scored = Bm25.Score(documents, "What was the domestic opening weekend of 3,571,150,070?", 3);
        Assert.Equal("b", scored[0].Id);
    }

    [Fact]
    public void Rrf_labels_which_arm_found_each_hit()
    {
        static VectorHit Hit(string id, double score) => new(id, score, new ChunkPayload
        {
            ChunkId = id, DocId = "d", Section = "", Text = ""
        });

        var fused = Rrf.Fuse([Hit("a", 0.9), Hit("b", 0.8)], [Hit("b", 4.0), Hit("c", 3.0)]);

        Assert.Equal("both", fused.First(f => f.Id == "b").Arm);
        Assert.Equal("vector", fused.First(f => f.Id == "a").Arm);
        Assert.Equal("keyword", fused.First(f => f.Id == "c").Arm);
        Assert.Equal("b", fused[0].Id);
    }
}

public class NameResolutionTests
{
    private static readonly NameNormalizer Normalizer = new(new DomainOptions());

    [Theory]
    [InlineData("The Thaw", "Thaw, The")]
    [InlineData("A Winter Passage", "Winter Passage")]
    [InlineData("Vermilion: Part II", "Vermilion Part 2")]
    [InlineData("Vermilion — Part 2", "Vermilion Part 2")]
    public void Title_normalisation_collapses_the_known_variants(string left, string right)
        => Assert.Equal(Normalizer.NormalizeTitle(left), Normalizer.NormalizeTitle(right));

    [Theory]
    [InlineData("Bob Vance", "Robert Vance")]
    [InlineData("Kate Okonjo", "Katherine Okonjo")]
    [InlineData("James Vance Jr", "James Vance")]
    public void Person_normalisation_expands_diminutives_and_drops_suffixes(string left, string right)
        => Assert.Equal(Normalizer.NormalizePerson(left), Normalizer.NormalizePerson(right));

    [Fact]
    public void Initials_merge_only_when_they_are_consistent()
    {
        Assert.True(Normalizer.InitialsCompatible("J. R. Vance", "James Robert Vance"));
        Assert.False(Normalizer.InitialsCompatible("J. R. Vance", "Peter Michael Vance"));
        Assert.False(Normalizer.InitialsCompatible("James Vance", "James Okonjo"));
    }

    [Fact]
    public void Studio_suffixes_are_stripped_for_comparison()
    {
        Assert.Equal(Normalizer.NormalizeStudio("Meridian Pictures"), Normalizer.NormalizeStudio("Meridian"));
        Assert.Equal(Normalizer.NormalizeStudio("Halcyon Films Ltd"), Normalizer.NormalizeStudio("Halcyon"));
    }

    /// <summary>
    /// The thresholds that matter are the configured ones: a one-letter misspelling of the same
    /// surname must clear 0.88, and two genuinely different surnames must not.
    /// </summary>
    [Fact]
    public void Jaro_winkler_separates_a_misspelling_from_a_different_name()
    {
        var misspelling = JaroWinkler.Similarity("gnanasekaran", "gnanasekeran");
        var different = JaroWinkler.Similarity("ranatunga", "rathnayake");

        Assert.True(misspelling > 0.88, $"A one-letter variant scored {misspelling:F3}.");
        Assert.True(different < 0.88, $"Two different surnames scored {different:F3}.");
        Assert.True(misspelling > different + 0.15);
    }
}

public class EntityKeyTests
{
    [Fact]
    public void Film_keys_carry_the_year_so_remakes_stay_apart()
    {
        var a = EntityKey.Build("Film", "Fantastic Four", 2005);
        var b = EntityKey.Build("Film", "Fantastic Four", 2015);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void A_person_and_a_character_with_the_same_name_can_never_collide()
    {
        var person = EntityKey.Build("Person", "Marlowe");
        var character = EntityKey.Build("Character", "Marlowe", workSlug: "the-thaw");
        Assert.NotEqual(person, character);
        Assert.Equal("Person", EntityKey.TypeOfKey(person));
        Assert.Equal("Character", EntityKey.TypeOfKey(character));
    }

    /// <summary>
    /// Trap 12 as the corpus states it: the series <em>Loki</em>, the character Loki, and the
    /// performer share one name. Three nodes, and the key prefixes make merging them impossible
    /// regardless of what any similarity threshold thinks.
    /// </summary>
    [Fact]
    public void One_name_held_by_three_types_yields_three_keys()
    {
        var series = EntityKey.Build("TVSeries", "Loki");
        var character = EntityKey.Build("Character", "Loki", workSlug: "loki");
        var person = EntityKey.Build("Person", "Loki");

        Assert.Equal(3, new[] { series, character, person }.Distinct().Count());
        Assert.Equal("TVSeries", EntityKey.TypeOfKey(series));
        Assert.Equal("Character", EntityKey.TypeOfKey(character));
        Assert.Equal("Person", EntityKey.TypeOfKey(person));
    }

    [Fact]
    public void Characters_are_scoped_to_their_work()
    {
        Assert.NotEqual(
            EntityKey.Build("Character", "Johnny Storm", workSlug: "fantastic-four-2005"),
            EntityKey.Build("Character", "Johnny Storm", workSlug: "fantastic-four-2015"));
    }
}

public class EntityResolverTests
{
    private static readonly DomainOptions Options = new();

    private static ProposedEntity Entity(string name, string type, int mentions = 1, string? workSlug = null, int? year = null)
    {
        var entity = new ProposedEntity
        {
            Key = EntityKey.Build(type, name, year, workSlug),
            Name = name, Type = type, Year = year, WorkSlug = workSlug, MentionCount = mentions,
        };
        entity.ChunkIds.Add("doc#1");
        return entity;
    }

    private static EntityResolver Resolver() =>
        new(Options, new NameNormalizer(Options), new HashEmbedder());

    /// <summary>
    /// Trap 12, end to end through the resolver. The series and the character must survive as two
    /// nodes, and the collision must be counted so the review UI can show the barrier doing work.
    /// </summary>
    [Fact]
    public async Task A_series_and_a_character_sharing_a_name_never_merge()
    {
        var outcome = await Resolver().ResolveAsync(
        [
            Entity("Loki", "TVSeries", 9),
            Entity("Loki", "Character", 6, workSlug: "loki"),
            Entity("Lahiru Senanayake", "Person", 4),
        ], new Dictionary<string, IReadOnlyList<string>>());

        Assert.Equal(3, outcome.Entities.Count);
        Assert.Single(outcome.Entities, e => e.Type == "TVSeries");
        Assert.Single(outcome.Entities, e => e.Type == "Character");
        Assert.Equal(1, outcome.CrossTypeNameCollisions);
    }

    [Fact]
    public async Task Two_films_sharing_a_title_stay_apart_when_their_years_differ()
    {
        var outcome = await Resolver().ResolveAsync(
        [
            Entity("Fantastic Four", "Film", 5, year: 2005),
            Entity("Fantastic Four", "Film", 4, year: 2015),
        ], new Dictionary<string, IReadOnlyList<string>>());

        Assert.Equal(2, outcome.Entities.Count);
        Assert.Equal([2005, 2015], outcome.Entities.Select(e => e.Year).OrderBy(y => y));
    }

    /// <summary>
    /// A sequel is not a surface form of its predecessor. "Spider-Man" and "Spider-Man 2" score
    /// about 0.97 Jaro-Winkler with near-identical embeddings, so before the year barrier was moved
    /// ahead of the fuzzy test the whole Raimi trilogy collapsed into one node and the sequels' cast
    /// and crew were re-attributed to the 2002 film.
    /// </summary>
    [Fact]
    public async Task Sequels_never_collapse_into_the_film_they_follow()
    {
        var outcome = await Resolver().ResolveAsync(
        [
            Entity("Spider-Man", "Film", 6, year: 2002),
            Entity("Spider-Man 2", "Film", 4, year: 2004),
            Entity("Spider-Man 3", "Film", 3, year: 2007),
        ], new Dictionary<string, IReadOnlyList<string>>());

        Assert.Equal(3, outcome.Entities.Count);
        Assert.Equal([2002, 2004, 2007], outcome.Entities.Select(e => e.Year).OrderBy(y => y));
    }

    /// <summary>The numeral decides even when the corpus never states a year.</summary>
    [Fact]
    public async Task A_sequel_numeral_separates_films_with_no_year_at_all()
    {
        var outcome = await Resolver().ResolveAsync(
        [
            Entity("The Amazing Spider-Man", "Film", 5),
            Entity("The Amazing Spider-Man 2", "Film", 3),
        ], new Dictionary<string, IReadOnlyList<string>>());

        Assert.Equal(2, outcome.Entities.Count);
    }

    [Fact]
    public async Task Surface_forms_of_one_person_collapse_and_keep_their_aliases()
    {
        var outcome = await Resolver().ResolveAsync(
        [
            Entity("Katherine Okonjo", "Person", 7),
            Entity("Kate Okonjo", "Person", 2),
        ], new Dictionary<string, IReadOnlyList<string>>());

        var person = Assert.Single(outcome.Entities);
        Assert.Equal("Katherine Okonjo", person.Name);
        Assert.Contains("Kate Okonjo", person.Aliases);
        Assert.Equal(9, person.MentionCount);
        Assert.Equal(2.0, outcome.SurfaceForms / (double)outcome.Entities.Count, 3);
    }

    /// <summary>
    /// Resolution rule 4's exception: the same person name holding incompatible roles in disjoint
    /// years is the review gate's business, not the resolver's.
    /// </summary>
    [Fact]
    public async Task Incompatible_role_clusters_are_flagged_for_a_human_rather_than_merged()
    {
        var full = Entity("James Robert Vance", "Person", 5);
        var initials = Entity("J. R. Vance", "Person", 3);

        var outcome = await Resolver().ResolveAsync([full, initials],
            new Dictionary<string, IReadOnlyList<string>>
            {
                [full.Key] = ["ACTED_IN", "y:1998"],
                [initials.Key] = ["COMPOSED_FOR", "y:2024"],
            });

        Assert.Equal(2, outcome.Entities.Count);
        var candidate = Assert.Single(outcome.AmbiguousMerges);
        Assert.Equal("Person", candidate.Type);
        Assert.Contains("disjoint years", candidate.Reason);
    }
}

public class ExtractionFilterTests
{
    private static readonly Ontology Ontology = Ontology.Default();

    [Fact]
    public void Grounding_requires_a_verbatim_span()
    {
        const string chunk = "Original score composed by Nadun Chandrasiri for Black Panther.";
        Assert.True(ExtractionFilters.IsGrounded("composed by Nadun Chandrasiri", chunk));
        Assert.True(ExtractionFilters.IsGrounded("COMPOSED   BY  Nadun Chandrasiri", chunk));
        Assert.False(ExtractionFilters.IsGrounded("scored by Nadun Chandrasiri", chunk));
    }

    [Fact]
    public void Grounding_tolerates_typographic_variants_but_not_new_words()
    {
        const string chunk = "The studio’s “second” unit — led by Ruvini — shot in Ella.";
        Assert.True(ExtractionFilters.IsGrounded("The studio's \"second\" unit - led by Ruvini", chunk));
        Assert.False(ExtractionFilters.IsGrounded("the second unit led by Nirmal", chunk));
    }

    [Fact]
    public void An_inverted_crew_credit_is_flipped_and_counted_not_dropped()
    {
        var relation = Relation("Film", "Black Panther", "DIRECTED", "Person", "Roshen Coorey");
        var outcome = ExtractionFilters.CheckDirection(relation, Ontology);

        Assert.True(outcome.Keep);
        Assert.True(outcome.Flipped);
        Assert.Equal("Roshen Coorey", relation.SubjectName);
        Assert.Equal("Black Panther", relation.ObjectName);
    }

    [Fact]
    public void Shot_by_runs_from_the_work_to_the_person()
    {
        var correct = Relation("Film", "Black Panther", "SHOT_BY", "Person", "Ruvini Hettiarachchi");
        Assert.False(ExtractionFilters.CheckDirection(correct, Ontology).Flipped);

        var inverted = Relation("Person", "Ruvini Hettiarachchi", "SHOT_BY", "Film", "Black Panther");
        Assert.True(ExtractionFilters.CheckDirection(inverted, Ontology).Flipped);
    }

    [Fact]
    public void An_impossible_endpoint_pair_is_dropped()
    {
        var relation = Relation("Studio", "Sinharaja Studios", "PLAYED", "Film", "Iron Man");
        var outcome = ExtractionFilters.CheckDirection(relation, Ontology);
        Assert.False(outcome.Keep);
        Assert.NotNull(outcome.DropReason);
    }

    [Fact]
    public void Deduplication_sums_mentions_and_keeps_the_highest_confidence()
    {
        var a = Relation("Person", "Arjun Sivalingam", "ACTED_IN", "Film", "Iron Man 2");
        a.Confidence = 0.7;
        a.ChunkIds.Add("doc#1");
        var b = Relation("Person", "Arjun Sivalingam", "ACTED_IN", "Film", "Iron Man 2");
        b.Confidence = 0.95;
        b.ChunkIds.Add("doc#2");

        var merged = Assert.Single(ExtractionFilters.Deduplicate([a, b]));
        Assert.Equal(2, merged.MentionCount);
        Assert.Equal(0.95, merged.Confidence, 3);
        Assert.Equal(2, merged.ChunkIds.Count);
    }

    private static ProposedRelation Relation(string subjectType, string subject, string predicate, string objectType, string obj) => new()
    {
        SubjectKey = EntityKey.Build(subjectType, subject),
        ObjectKey = EntityKey.Build(objectType, obj),
        SubjectName = subject,
        ObjectName = obj,
        SubjectType = subjectType,
        ObjectType = objectType,
        Predicate = predicate,
        Confidence = 0.9,
        Evidence = "evidence",
    };
}

public class StagePresetTests
{
    private static readonly RagLadderOptions Config = new();

    [Fact]
    public void Stage_zero_skips_retrieval_entirely()
    {
        var options = StagePresets.For(0, Config);
        Assert.True(options.SkipRetrieval);
        Assert.False(options.UseHybrid);
    }

    [Fact]
    public void Presets_are_cumulative()
    {
        for (var stage = 1; stage <= StagePresets.MaxStage; stage++)
        {
            var options = StagePresets.For(stage, Config);
            if (stage >= 3) Assert.True(options.UseMetadataFilter);
            if (stage >= 4) Assert.True(options.UseHybrid);
            if (stage >= 5) Assert.True(options.UseRerank);
            if (stage >= 6) Assert.True(options.UseQueryRewrite);
            if (stage >= 8) Assert.True(options.RequireCitations);
            if (stage >= 9) Assert.True(options.UseAgentic);
            if (stage >= 10) Assert.True(options.UseGraphExpansion);
            if (stage >= 11) Assert.True(options.UseRouter);
        }
    }

    [Fact]
    public void Collections_change_at_the_stages_that_teach_them()
    {
        Assert.Equal(ChunkStrategies.Fixed, StagePresets.For(1, Config).Collection);
        Assert.Equal(ChunkStrategies.Recursive, StagePresets.For(2, Config).Collection);
        Assert.Equal(ChunkStrategies.Recursive, StagePresets.For(6, Config).Collection);
        Assert.Equal(ChunkStrategies.Contextual, StagePresets.For(7, Config).Collection);
    }

    [Fact]
    public void Wide_retrieval_only_arrives_with_reranking()
    {
        Assert.Equal(StagePresets.For(4, Config).TopK, StagePresets.For(4, Config).CandidateK);
        Assert.Equal(50, StagePresets.For(5, Config).CandidateK);
    }

    /// <summary>Flow isolation: no two rungs may produce the same answer-cache key (spec §7.4).</summary>
    [Fact]
    public void Every_stage_has_a_distinct_cache_key()
    {
        var keys = Enumerable.Range(0, StagePresets.MaxStage + 1)
            .Select(s => AnswerGenerator.CacheScopeFor("doc_1", "Who scored Black Panther?", StagePresets.For(s, Config)))
            .ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }
}

public class CitationTests
{
    [Fact]
    public void A_supported_citation_is_verified_and_quoted()
    {
        var context = new List<RetrievedChunk>
        {
            new() { ChunkId = "c1", Text = "Original score composed by Nadun Chandrasiri. Locations Kandy, Ella.", Page = 3, Section = "Black Panther (2018)" }
        };
        var result = CitationChecker.Check("The score was composed by Nadun Chandrasiri [1].", context);

        var citation = Assert.Single(result.Citations);
        Assert.True(citation.Verified);
        Assert.Equal(1.0, result.Groundedness, 3);
    }

    [Fact]
    public void A_citation_pointing_at_unrelated_text_is_flagged()
    {
        var context = new List<RetrievedChunk>
        {
            new() { ChunkId = "c1", Text = "Production facilities are at Ratmalana and Katunayake.", Page = 1, Section = "Studio history" }
        };
        var result = CitationChecker.Check("The composer was Suranga Bogollagama for Endgame in 2019 [1].", context);

        Assert.False(Assert.Single(result.Citations).Verified);
        Assert.NotNull(result.Warning);
    }
}

public class MetadataFilterTests
{
    private static SectionRecord Section(int ordinal, string heading, string docType, string subject, int year) => new()
    {
        Id = $"doc#s{ordinal}", DocId = "doc", Ordinal = ordinal, Heading = heading,
        StartChar = 0, EndChar = 100, Page = 1, Text = heading,
        FrontMatter = new FrontMatter { DocType = docType, Subject = subject, Year = year },
    };

    private static readonly List<SectionRecord> Sections =
    [
        Section(0, "Fantastic Four (2005)", "title-record", "Fantastic Four", 2005),
        Section(1, "Fantastic Four (2015)", "title-record", "Fantastic Four", 2015),
        Section(2, "Box Office Record", "box-office-record", "Avengers: Endgame", 2019),
    ];

    [Fact]
    public void A_year_in_the_question_becomes_a_year_filter()
    {
        var filter = MetadataFilterInference.Infer("Who played Johnny Storm in Fantastic Four (2015)?", Sections);
        Assert.Equal(2015, filter.Year);
        Assert.Equal("Fantastic Four", filter.Subject);
    }

    [Fact]
    public void A_box_office_question_picks_the_matching_docType()
    {
        var filter = MetadataFilterInference.Infer("What was the domestic opening weekend gross?", Sections);
        Assert.Equal("box-office-record", filter.DocType);
    }

    [Fact]
    public void Only_docTypes_the_document_actually_uses_are_applied()
    {
        var filter = MetadataFilterInference.Infer("Which festival premiered it?", Sections);
        Assert.Null(filter.DocType);
    }
}

public class PathNarrativeTests
{
    [Fact]
    public void A_traversal_reads_as_prose_in_the_direction_it_was_crossed()
    {
        var nodes = new List<PathNode>
        {
            new() { Key = "person:a", Name = "Sunil Gunatilleke", Type = "Person" },
            new() { Key = "film:ff:2005", Name = "Fantastic Four", Type = "Film", Year = 2005 },
            new() { Key = "person:b", Name = "Kasun Jayawardena", Type = "Person" },
        };
        var narrative = PathNarrative.Render(nodes, ["ACTED_IN", "ACTED_IN"], Ontology.Default());

        Assert.Contains("Sunil Gunatilleke acted in Fantastic Four (2005)", narrative);
        Assert.Contains("which starred Kasun Jayawardena", narrative);
        Assert.DoesNotContain("who which", narrative);
    }

    /// <summary>The relative pronoun has to agree with the node the clause continues from.</summary>
    [Fact]
    public void A_continuing_clause_opens_with_the_right_pronoun()
    {
        var nodes = new List<PathNode>
        {
            new() { Key = "person:a", Name = "Ilse Vantor", Type = "Person" },
            new() { Key = "film:t:2024", Name = "The Thaw", Type = "Film", Year = 2024 },
            new() { Key = "person:d", Name = "Dara Okonjo", Type = "Person" },
            new() { Key = "film:v:2024", Name = "Vermilion", Type = "Film", Year = 2024 },
        };
        var narrative = PathNarrative.Render(nodes, ["ACTED_IN", "DIRECTED", "DIRECTED"], Ontology.Default());

        Assert.DoesNotContain("who which", narrative);
        Assert.DoesNotContain("which who", narrative);
        Assert.Contains("which was directed by Dara Okonjo", narrative);
        Assert.Contains("who directed Vermilion", narrative);
    }
}

public class JsonTextTests
{
    [Fact]
    public void Extracts_an_object_from_a_fenced_reply()
    {
        var json = JsonText.ExtractObject("Here you go:\n```json\n{\"a\": {\"b\": 1}}\n```\nHope that helps.");
        Assert.Equal("{\"a\": {\"b\": 1}}", json);
    }

    [Fact]
    public void Braces_inside_strings_do_not_end_the_object()
    {
        var json = JsonText.ExtractObject("{\"evidence\": \"a } brace\", \"n\": 1}");
        Assert.Equal("{\"evidence\": \"a } brace\", \"n\": 1}", json);
    }
}

/// <summary>
/// The answer cache is what makes the ladder demonstrable: a cold rung costs minutes on a CPU
/// model, so the twelve answers behind a demo have to survive a restart. Unbounded growth would
/// make that table the largest thing in the database, hence the LRU bound.
/// </summary>
public class AnswerCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ragladder-cache-" + Guid.NewGuid().ToString("N"));
    private readonly CacheRepository _cache;

    public AnswerCacheTests()
    {
        var options = new RagLadderOptions();
        options.Storage.DataDirectory = _dir;
        _cache = new CacheRepository(new Db(Options.Create(options)));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* the file lock is the OS's business, not the test's */ }
    }

    [Fact]
    public void An_answer_survives_and_is_returned_verbatim()
    {
        _cache.PutAnswer("k1", "doc1", "Who plays Peter Parker?", 3, "{\"answer\":\"Niraj Ranasinghe\"}");
        Assert.Equal("{\"answer\":\"Niraj Ranasinghe\"}", _cache.GetAnswer("k1"));
        Assert.Null(_cache.GetAnswer("nope"));
    }

    [Fact]
    public void The_cache_is_bounded_and_evicts_the_least_recently_used()
    {
        for (var i = 0; i < CacheRepository.AnswerCacheLimit + 12; i++)
            _cache.PutAnswer($"k{i}", "doc1", $"question {i}", i % 12, $"payload {i}");

        Assert.Equal(CacheRepository.AnswerCacheLimit, _cache.AnswerCount());

        // The earliest writes are the ones that went.
        Assert.Null(_cache.GetAnswer("k0"));
        Assert.Equal($"payload {CacheRepository.AnswerCacheLimit + 11}",
            _cache.GetAnswer($"k{CacheRepository.AnswerCacheLimit + 11}"));
    }

    [Fact]
    public void Reading_an_answer_keeps_it_from_being_evicted()
    {
        _cache.PutAnswer("keeper", "doc1", "the demo question", 0, "payload");
        for (var i = 0; i < CacheRepository.AnswerCacheLimit - 1; i++)
            _cache.PutAnswer($"filler{i}", "doc1", $"q{i}", 1, "x");

        // One read lifts it above the fillers, so the next write evicts a filler instead.
        Assert.Equal("payload", _cache.GetAnswer("keeper"));
        _cache.PutAnswer("newest", "doc1", "another", 2, "y");

        Assert.Equal("payload", _cache.GetAnswer("keeper"));
        Assert.Equal(CacheRepository.AnswerCacheLimit, _cache.AnswerCount());
    }

    [Fact]
    public void Answers_can_be_listed_and_cleared_per_document()
    {
        _cache.PutAnswer("a", "doc1", "one", 0, "x");
        _cache.PutAnswer("b", "doc2", "two", 1, "y");

        Assert.Single(_cache.ListAnswers("doc1"));
        Assert.Equal(2, _cache.ListAnswers().Count);

        _cache.ClearAnswers("doc1");
        Assert.Empty(_cache.ListAnswers("doc1"));
        Assert.Single(_cache.ListAnswers());
    }
}
