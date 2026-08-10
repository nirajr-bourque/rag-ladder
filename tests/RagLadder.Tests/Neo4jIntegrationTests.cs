using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RagLadder.Api.Configuration;
using RagLadder.Api.Graph;
using RagLadder.Api.Llm;
using RagLadder.Api.Models;
using Xunit;

namespace RagLadder.Tests;

/// <summary>
/// Exercises the Neo4j implementation against a real instance.
///
/// Skipped unless credentials are present, so the default `dotnet test` stays offline:
///
///   $env:RAGLADDER_TEST_NEO4J_URI      = 'neo4j+s://xxxx.databases.neo4j.io'
///   $env:RAGLADDER_TEST_NEO4J_PASSWORD = '...'
///   dotnet test --filter Neo4j
///
/// Everything it writes is namespaced to a throwaway document id and deleted afterwards, so it is
/// safe to point at the same instance you demo from.
/// </summary>
public sealed class Neo4jIntegrationTests : IAsyncLifetime
{
    private readonly string? _uri = Environment.GetEnvironmentVariable("RAGLADDER_TEST_NEO4J_URI");
    private readonly string? _password = Environment.GetEnvironmentVariable("RAGLADDER_TEST_NEO4J_PASSWORD");
    private readonly string _docId = "doc_test_" + Guid.NewGuid().ToString("N")[..8];
    private Neo4jGraphStore? _store;

    private bool Configured => !string.IsNullOrWhiteSpace(_uri) && !string.IsNullOrWhiteSpace(_password);

    public Task InitializeAsync()
    {
        if (!Configured) return Task.CompletedTask;

        var options = Options.Create(new RagLadderOptions
        {
            Neo4j = new Neo4jOptions
            {
                Uri = _uri!,
                User = Environment.GetEnvironmentVariable("RAGLADDER_TEST_NEO4J_USER") ?? "neo4j",
                Password = _password!,
                Database = Environment.GetEnvironmentVariable("RAGLADDER_TEST_NEO4J_DATABASE") ?? "neo4j",
            },
        });
        _store = new Neo4jGraphStore(options, Ontology.Default(), NullLogger<Neo4jGraphStore>.Instance);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_store is null) return;
        try { await _store.DeleteDocumentAsync(_docId); } catch { /* best effort cleanup */ }
        await _store.DisposeAsync();
    }

    [SkippableFact]
    public async Task The_whole_graph_round_trips_through_a_real_instance()
    {
        Skip.If(!Configured, "Set RAGLADDER_TEST_NEO4J_URI and RAGLADDER_TEST_NEO4J_PASSWORD to run this.");
        var store = _store!;

        var health = await store.HealthAsync();
        Assert.Equal(ProviderHealth.Ok, health.Status);

        await store.EnsureSchemaAsync();
        await store.CommitAsync(BuildCommit());

        // ----- derived edges ------------------------------------------------
        var derived = await store.ComputeDerivedEdgesAsync(_docId);
        Assert.True(derived >= 0);

        var snapshot = await store.SnapshotAsync(_docId, 0, includeDerived: true, 500);
        Assert.True(snapshot.Nodes.Count >= 5, $"Expected the committed nodes back, got {snapshot.Nodes.Count}.");
        Assert.Contains(snapshot.Edges, e => e.Predicate == "ACTED_IN");
        Assert.Contains(snapshot.Edges, e => e.Predicate == "SHOT_BY");
        Assert.Contains(snapshot.Edges, e => e.Derived && e.Predicate == "COLLABORATED_WITH");

        // ----- the type barrier survives a real write -----------------------
        var loki = await store.SearchEntitiesAsync(_docId, null, "Loki", 20);
        var types = loki.Select(e => e.Type).Distinct().ToList();
        Assert.True(types.Count >= 2,
            $"A Character and a TVSeries both named Loki must stay separate; found only {string.Join(",", types)}.");

        // ----- shortest path ------------------------------------------------
        var path = await store.ShortestPathAsync(
            _docId, EntityKey.Build("Person", "Ilse Vantor"), EntityKey.Build("Person", "Piet Hansen"), 8, 0);
        Assert.NotNull(path);
        Assert.True(path!.Hops >= 2, "The two never shared a title, so the path must run through a work.");
        Assert.Contains(path.Nodes, n => n.Type is "Film");
        Assert.False(string.IsNullOrWhiteSpace(path.Narrative));

        // ----- aggregation --------------------------------------------------
        var aggregate = await store.AggregateAsync(_docId, AggregationPresets.StudioFilmCount, null, 0);
        Assert.Contains("MATCH", aggregate.Cypher);
        Assert.NotEmpty(aggregate.Rows);

        var pairs = await store.AggregateAsync(_docId, AggregationPresets.DirectorCinematographerPairs, null, 0);
        Assert.NotEmpty(pairs.Rows);

        // ----- edge lookup with evidence ------------------------------------
        var edge = await store.GetEdgeAsync(
            _docId, EntityKey.Build("Person", "Ilse Vantor"), "ACTED_IN", EntityKey.Build("Film", "The Thaw", 2024));
        Assert.NotNull(edge);
        Assert.Equal("Ilse Vantor", edge!.FromName);
        Assert.False(string.IsNullOrWhiteSpace(edge.Evidence));

        // ----- delete removes everything for this document -------------------
        await store.DeleteDocumentAsync(_docId);
        var afterDelete = await store.SnapshotAsync(_docId, 0, includeDerived: true, 500);
        Assert.Empty(afterDelete.Nodes);
    }

    /// <summary>
    /// A miniature of the real corpus: two films sharing a director, two performers who never
    /// share a title, a cinematographer credit that runs work → person, and a Character and a
    /// TVSeries with the same name.
    /// </summary>
    private GraphCommit BuildCommit()
    {
        ProposedEntity Entity(string name, string type, int? year = null, string? work = null)
        {
            var entity = new ProposedEntity
            {
                Key = EntityKey.Build(type, name, year, work),
                Name = name, Type = type, Year = year, WorkSlug = work, MentionCount = 2,
            };
            entity.ChunkIds.Add(_docId + "#0");
            return entity;
        }

        ProposedRelation Relation(ProposedEntity from, string predicate, ProposedEntity to)
        {
            var relation = new ProposedRelation
            {
                SubjectKey = from.Key, ObjectKey = to.Key,
                SubjectName = from.Name, ObjectName = to.Name,
                SubjectType = from.Type, ObjectType = to.Type,
                Predicate = predicate, Confidence = 0.9,
                Evidence = $"{from.Name} {predicate} {to.Name}",
            };
            relation.ChunkIds.Add(_docId + "#0");
            return relation;
        }

        var vantor = Entity("Ilse Vantor", "Person");
        var hansen = Entity("Piet Hansen", "Person");
        var okonjo = Entity("Dara Okonjo", "Person");
        var lindqvist = Entity("Ana Lindqvist", "Person");
        var thaw = Entity("The Thaw", "Film", 2024);
        var vermilion = Entity("Vermilion", "Film", 2024);
        var studio = Entity("Meridian Pictures", "Studio");
        var lokiSeries = Entity("Loki", "TVSeries");
        var lokiCharacter = Entity("Loki", "Character", work: "loki");

        var entities = new List<ProposedEntity>
        {
            vantor, hansen, okonjo, lindqvist, thaw, vermilion, studio, lokiSeries, lokiCharacter
        };

        var relations = new List<ProposedRelation>
        {
            Relation(vantor, "ACTED_IN", thaw),
            Relation(okonjo, "DIRECTED", thaw),
            Relation(okonjo, "DIRECTED", vermilion),
            Relation(hansen, "ACTED_IN", vermilion),
            Relation(thaw, "SHOT_BY", lindqvist),
            Relation(vermilion, "SHOT_BY", lindqvist),
            Relation(thaw, "PRODUCED_BY", studio),
            Relation(vermilion, "PRODUCED_BY", studio),
        };

        var section = new SectionRecord
        {
            Id = _docId + "#s0", DocId = _docId, Ordinal = 0, Heading = "Test section",
            StartChar = 0, EndChar = 10, Page = 1, Text = "test",
            FrontMatter = new FrontMatter { DocType = "title-record", Subject = "The Thaw", Year = 2024 },
        };

        var chunk = new ChunkRecord
        {
            Id = _docId + "#0", DocId = _docId, Strategy = ChunkStrategies.Recursive,
            Seq = 0, StrategyOrdinal = 0, SectionId = section.Id, Page = 1,
            StartChar = 0, EndChar = 10, Text = "test", RawText = "test",
            FrontMatter = section.FrontMatter,
        };

        return new GraphCommit
        {
            Document = new DocumentRecord
            {
                Id = _docId, Title = "Neo4j integration test", FileName = "test.pdf",
                PageCount = 1, UploadedUtc = DateTimeOffset.UtcNow,
            },
            Sections = [section],
            Chunks = [chunk],
            Entities = entities,
            Relations = relations,
        };
    }
}
