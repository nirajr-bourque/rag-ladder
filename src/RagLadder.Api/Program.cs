using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using RagLadder.Api.Ask;
using RagLadder.Api.Chunking;
using RagLadder.Api.Configuration;
using RagLadder.Api.Embedding;
using RagLadder.Api.Endpoints;
using RagLadder.Api.Eval;
using RagLadder.Api.Extraction;
using RagLadder.Api.Graph;
using RagLadder.Api.Health;
using RagLadder.Api.Infrastructure;
using RagLadder.Api.Llm;
using RagLadder.Api.Models;
using RagLadder.Api.Parsing;
using RagLadder.Api.Pipeline;
using RagLadder.Api.Reranking;
using RagLadder.Api.Vector;

// Anchor the content root to the binary's own directory rather than the working directory, so
// appsettings.json and wwwroot are found however the app was launched. Configured data paths are
// separately resolved against the repository root by RepoPaths.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = File.Exists(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
        ? AppContext.BaseDirectory
        : Directory.GetCurrentDirectory(),
});

// --replay serves recorded model responses and never touches the network (spec §12).
var replay = args.Contains("--replay");
var record = args.Contains("--record");

builder.Services.Configure<RagLadderOptions>(builder.Configuration.GetSection(RagLadderOptions.SectionName));
builder.Services.PostConfigure<RagLadderOptions>(o =>
{
    if (replay) o.Replay.Enabled = true;
    if (record) o.Replay.Record = true;
    RepoPaths.ResolveAll(o);
});

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.WriteIndented = false;
});

// ----- storage ---------------------------------------------------------------

builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<CorpusRepository>();
builder.Services.AddSingleton<CacheRepository>();
builder.Services.AddSingleton<ReviewRepository>();

// ----- domain ----------------------------------------------------------------

builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<RagLadderOptions>>().Value;
    return Ontology.LoadOrDefault(options.Domain.OntologyPath);
});
builder.Services.AddSingleton(sp =>
    new NameNormalizer(sp.GetRequiredService<IOptions<RagLadderOptions>>().Value.Domain));

// ----- embedding and reranking ------------------------------------------------
// The ONNX models are large and not committed. When they are absent the app falls back to a
// deterministic dev stand-in so the pipeline still runs end to end, and health reports the
// degradation loudly so it can never be mistaken for the real thing during a demo.

builder.Services.AddHttpClient<OllamaEmbedder>();

builder.Services.AddSingleton<IEmbedder>(sp =>
{
    var options = sp.GetRequiredService<IOptions<RagLadderOptions>>().Value;
    var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Embedder");
    var cache = sp.GetRequiredService<CacheRepository>();

    IEmbedder inner;
    if (options.Providers.Embedder.Equals("hash", StringComparison.OrdinalIgnoreCase))
    {
        inner = new HashEmbedder(options.Embedding.Dimensions);
    }
    else if (options.Providers.Embedder.Equals("ollama", StringComparison.OrdinalIgnoreCase))
    {
        // For networks where huggingface.co is blocked but the Ollama endpoint is reachable.
        inner = sp.GetRequiredService<OllamaEmbedder>();
        log.LogInformation("Embedder: {Model} served by Ollama (no local model file).", options.Embedding.OllamaModel);
    }
    else
    {
        try
        {
            inner = new OnnxEmbedder(options.Embedding);
            log.LogInformation("Embedder: {Model} (ONNX).", options.Embedding.ModelId);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DllNotFoundException or InvalidOperationException)
        {
            if (!options.Providers.FallbackToLocal) throw;
            log.LogWarning("ONNX embedder unavailable ({Message}). Falling back to the deterministic dev embedder — " +
                           "run tools/fetch-models.ps1 before demoing.", ex.Message);
            inner = new HashEmbedder(options.Embedding.Dimensions);
        }
    }
    return new CachingEmbedder(inner, cache);
});

builder.Services.AddSingleton<IReranker>(sp =>
{
    var options = sp.GetRequiredService<IOptions<RagLadderOptions>>().Value;
    var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Reranker");
    if (options.Providers.Reranker.Equals("lexical", StringComparison.OrdinalIgnoreCase))
        return new LexicalReranker();
    if (options.Providers.Reranker.Equals("llm", StringComparison.OrdinalIgnoreCase))
    {
        // Cross-encoder behaviour without the cross-encoder file: the model reads query and
        // passage together, which is the property stage 5 is actually teaching.
        log.LogInformation("Reranker: chat model scoring (no local cross-encoder file).");
        return new LlmReranker(sp.GetRequiredService<IChatClient>(), sp.GetRequiredService<ILogger<LlmReranker>>());
    }
    try
    {
        var reranker = new OnnxReranker(options.Rerank);
        log.LogInformation("Reranker: {Model} (ONNX cross-encoder).", options.Rerank.ModelId);
        return reranker;
    }
    catch (Exception ex) when (ex is FileNotFoundException or DllNotFoundException or InvalidOperationException)
    {
        if (!options.Providers.FallbackToLocal) throw;
        log.LogWarning("ONNX reranker unavailable ({Message}). Falling back to the lexical dev reranker.", ex.Message);
        return new LexicalReranker();
    }
});

// ----- chat -------------------------------------------------------------------

builder.Services.AddHttpClient<OllamaChatClient>();
builder.Services.AddHttpClient<OpenAiCompatibleChatClient>();
builder.Services.AddSingleton<ReplayChatClient>();

builder.Services.AddSingleton<IChatClient>(sp =>
{
    var options = sp.GetRequiredService<IOptions<RagLadderOptions>>().Value;
    var cache = sp.GetRequiredService<CacheRepository>();
    var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Chat");

    IChatClient client;
    if (options.Replay.Enabled || options.Providers.Chat.Equals("replay", StringComparison.OrdinalIgnoreCase))
    {
        client = sp.GetRequiredService<ReplayChatClient>();
    }
    else if (options.Providers.Chat.Equals("openai", StringComparison.OrdinalIgnoreCase))
    {
        // Any OpenAI-compatible endpoint: Azure OpenAI, an internal gateway, vLLM, LM Studio.
        // The escape hatch for networks that block ollama.com.
        log.LogInformation("Chat: OpenAI-compatible endpoint at {BaseUrl}.", options.OpenAiCompatible.BaseUrl);
        client = sp.GetRequiredService<OpenAiCompatibleChatClient>();
    }
    else
    {
        client = sp.GetRequiredService<OllamaChatClient>();
        if (options.Replay.Record)
            client = new RecordingChatClient(client, sp.GetRequiredService<IOptions<RagLadderOptions>>(),
                sp.GetRequiredService<ILogger<RecordingChatClient>>());
    }
    return new CachingChatClient(client, cache);
});

// ----- vector store -------------------------------------------------------------

builder.Services.AddHttpClient<QdrantVectorStore>();
builder.Services.AddSingleton<LocalVectorStore>();

builder.Services.AddSingleton<IVectorStore>(sp =>
{
    var options = sp.GetRequiredService<IOptions<RagLadderOptions>>().Value;
    var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("VectorStore");

    if (!options.Providers.Vector.Equals("qdrant", StringComparison.OrdinalIgnoreCase))
        return sp.GetRequiredService<LocalVectorStore>();

    if (string.IsNullOrWhiteSpace(options.Qdrant.Url))
    {
        if (!options.Providers.FallbackToLocal)
            throw new InvalidOperationException("Providers:Vector is 'qdrant' but Qdrant:Url is empty.");
        log.LogWarning("Qdrant selected but no URL configured. Using the local SQLite vector store.");
        return sp.GetRequiredService<LocalVectorStore>();
    }
    return sp.GetRequiredService<QdrantVectorStore>();
});

// ----- graph store ---------------------------------------------------------------

builder.Services.AddSingleton<LocalGraphStore>();

builder.Services.AddSingleton<IGraphStore>(sp =>
{
    var options = sp.GetRequiredService<IOptions<RagLadderOptions>>().Value;
    var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("GraphStore");

    if (!options.Providers.Graph.Equals("neo4j", StringComparison.OrdinalIgnoreCase))
        return sp.GetRequiredService<LocalGraphStore>();

    if (string.IsNullOrWhiteSpace(options.Neo4j.Uri))
    {
        if (!options.Providers.FallbackToLocal)
            throw new InvalidOperationException("Providers:Graph is 'neo4j' but Neo4j:Uri is empty.");
        log.LogWarning("Neo4j selected but no URI configured. Using the local SQLite graph store.");
        return sp.GetRequiredService<LocalGraphStore>();
    }

    return new Neo4jGraphStore(
        sp.GetRequiredService<IOptions<RagLadderOptions>>(),
        sp.GetRequiredService<Ontology>(),
        sp.GetRequiredService<ILogger<Neo4jGraphStore>>());
});

// ----- pipeline and services -------------------------------------------------------

builder.Services.AddSingleton<PdfDocumentParser>();
builder.Services.AddSingleton<SectionSegmenter>();
builder.Services.AddSingleton(sp => new EntityResolver(
    sp.GetRequiredService<IOptions<RagLadderOptions>>().Value.Domain,
    sp.GetRequiredService<NameNormalizer>(),
    sp.GetRequiredService<IEmbedder>()));
builder.Services.AddSingleton<ExtractionService>();
builder.Services.AddSingleton<ProcessingService>();

builder.Services.AddSingleton<Retriever>();
builder.Services.AddSingleton<QueryRewriter>();
builder.Services.AddSingleton<GraphStageService>();
builder.Services.AddSingleton<AgenticLoop>();
builder.Services.AddSingleton<QueryRouter>();
builder.Services.AddSingleton<AnswerGenerator>();
builder.Services.AddSingleton<AskService>();
builder.Services.AddSingleton<EvalService>();
builder.Services.AddSingleton<HealthService>();

var app = builder.Build();

app.UseMiddleware<ParseExceptionMiddleware>();
app.UseDefaultFiles();

// The UI is edited live and has no build step or content hashing, so a cached app.js against a
// newer index.html (or the reverse) silently produces a half-dead page — the kind that shows
// "loading…" forever and swallows clicks. `no-cache` still allows the 304 revalidation round trip,
// so this costs one conditional GET per file, not a re-download. Correctness over a few bytes.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers.CacheControl = "no-cache, must-revalidate";
    },
});

app.MapDocumentEndpoints();
app.MapReviewEndpoints();
app.MapExternalExtractionEndpoints();
app.MapAskEndpoints();
app.MapGraphEndpoints();
app.MapEvalEndpoints();

app.MapGet("/api/health", async (HealthService health, CancellationToken ct) =>
{
    var report = await health.CheckAsync(ct);
    return report.Status == "unhealthy"
        ? Results.Json(report, statusCode: StatusCodes.Status503ServiceUnavailable)
        : Results.Ok(report);
}).WithTags("health");

app.MapGet("/api/config", (IOptions<RagLadderOptions> options) =>
{
    var o = options.Value;
    return Results.Ok(new
    {
        providers = o.Providers,
        chunking = o.Chunking,
        extraction = o.Extraction,
        retrieval = o.Retrieval,
        agentic = o.Agentic,
        domain = o.Domain,
        models = new { chat = o.Ollama.ChatModel, extraction = o.Ollama.ExtractionModel, embedding = o.Embedding.ModelId, rerank = o.Rerank.ModelId },
        replay = o.Replay,
    });
}).WithTags("health");

// Log the startup posture once — which providers are live matters more on demo day than anything
// else in the log.
{
    using var scope = app.Services.CreateScope();
    var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    var health = scope.ServiceProvider.GetRequiredService<HealthService>();
    var report = await health.CheckAsync(CancellationToken.None);
    log.LogInformation("RAG Ladder starting — status {Status}.", report.Status);
    foreach (var provider in report.Providers)
        log.LogInformation("  {Name,-9} {Status,-14} {Detail}", provider.Name, provider.Status, provider.Detail);
}

app.Run();

public partial class Program;
