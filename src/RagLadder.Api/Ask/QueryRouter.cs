using System.Text.Json;
using RagLadder.Api.Llm;
using RagLadder.Api.Models;

namespace RagLadder.Api.Ask;

public sealed record RoutingOutcome(RouterBlock Block, AskOptions Options, int Calls, string? Warning);

/// <summary>
/// Stage 11. Not every query needs every layer: a simple lookup does not need the agentic loop,
/// and a path question does not need hybrid search at all. The classification and the chosen route
/// are both recorded in the trace, including when the router gets it wrong — which is more honest
/// and more memorable than only ever showing it right (spec §7.5).
/// </summary>
public sealed class QueryRouter(IChatClient chat)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public const string Lookup = "lookup";
    public const string Relational = "relational";
    public const string Path = "path";
    public const string Aggregation = "aggregation";
    public const string MultiPart = "multi_part";

    public async Task<RoutingOutcome> RouteAsync(string question, AskOptions baseOptions, CancellationToken ct)
    {
        var response = await chat.CompleteAsync(new ChatRequest
        {
            Model = chat.ChatModel,
            Messages =
            [
                ChatMessage.System("""
                    Classify the question into exactly one category and return JSON only.

                      lookup      — one fact from one place. "Who composed the score for X?"
                      relational  — needs relationships between entities, but not a chain.
                                    "Which cinematographer has shot more than one film for director D?"
                      path        — how two people are connected. "How is A connected to B?"
                      aggregation — counting or ranking across the whole collection.
                                    "Which studio released the most films in 2024?"
                      multi_part  — two or more separate facts that must be gathered separately.
                                    "Compare the opening weekends of A and B."

                    {"classification":"lookup","rationale":"one short sentence"}
                    """),
                ChatMessage.User(question)
            ],
            Temperature = 0,
            JsonOnly = true,
            Purpose = ChatPurpose.Routing,
        }, ct);

        if (response.Failed)
        {
            return new RoutingOutcome(
                new RouterBlock
                {
                    Classification = "unknown",
                    Route = "full-pipeline",
                    Rationale = "Classification call failed; every layer left enabled.",
                },
                baseOptions, 0, $"Router failed: {response.Warning}");
        }

        var parsed = JsonText.TryDeserialize<Classification>(response.Content, Json);
        var classification = (parsed?.ClassificationValue ?? Lookup).ToLowerInvariant();
        var options = baseOptions.Clone();
        var applied = new List<string>();

        switch (classification)
        {
            case Path:
                options.UseGraphExpansion = true;
                options.GraphMode = GraphModes.Path;
                options.UseAgentic = false;
                options.UseRerank = false;
                options.UseHybrid = false;
                applied.AddRange(["graphMode=path", "agentic off", "rerank off", "hybrid off"]);
                break;

            case Aggregation:
                options.UseGraphExpansion = true;
                options.GraphMode = GraphModes.Aggregate;
                options.UseAgentic = false;
                options.AggregationPreset ??= GraphStageService.GuessPreset(question);
                applied.AddRange(["graphMode=aggregate", "agentic off", $"preset={options.AggregationPreset}"]);
                break;

            case Relational:
                options.UseGraphExpansion = true;
                options.GraphMode = GraphModes.Expand;
                options.GraphHops = new GraphHops { Next = true, Parent = true, Entity = true, EntityRel = true };
                options.UseAgentic = false;
                applied.AddRange(["graphMode=expand", "entity+entityRel hops on", "agentic off"]);
                break;

            case MultiPart:
                options.UseAgentic = true;
                options.GraphMode = GraphModes.Expand;
                applied.Add("agentic on");
                break;

            default:
                classification = Lookup;
                options.UseAgentic = false;
                options.UseGraphExpansion = false;
                applied.AddRange(["agentic off", "graph off"]);
                break;
        }

        return new RoutingOutcome(
            new RouterBlock
            {
                Classification = classification,
                Route = RouteName(classification),
                Rationale = parsed?.Rationale,
                AppliedFlags = applied,
            },
            options,
            response.FromCache ? 0 : 1,
            null);
    }

    private static string RouteName(string classification) => classification switch
    {
        Path => "graph:path",
        Aggregation => "graph:aggregate",
        Relational => "graph:expand",
        MultiPart => "agentic",
        _ => "vector-only"
    };

    private sealed class Classification
    {
        [System.Text.Json.Serialization.JsonPropertyName("classification")]
        public string? ClassificationValue { get; set; }
        public string? Rationale { get; set; }
    }
}
