namespace RagLadder.Api.Configuration;

/// <summary>Root configuration. Bound from the "RagLadder" section of appsettings.json.</summary>
public sealed class RagLadderOptions
{
    public const string SectionName = "RagLadder";

    public ProvidersOptions Providers { get; set; } = new();
    public StorageOptions Storage { get; set; } = new();
    public EmbeddingOptions Embedding { get; set; } = new();
    public RerankOptions Rerank { get; set; } = new();
    public OllamaOptions Ollama { get; set; } = new();
    public OpenAiCompatibleOptions OpenAiCompatible { get; set; } = new();
    public QdrantOptions Qdrant { get; set; } = new();
    public Neo4jOptions Neo4j { get; set; } = new();
    public ChunkingOptions Chunking { get; set; } = new();
    public ExtractionOptions Extraction { get; set; } = new();
    public RetrievalOptions Retrieval { get; set; } = new();
    public AgenticOptions Agentic { get; set; } = new();
    public DomainOptions Domain { get; set; } = new();
    public ReplayOptions Replay { get; set; } = new();
}

public sealed class ProvidersOptions
{
    /// <summary>qdrant | memory</summary>
    public string Vector { get; set; } = "memory";
    /// <summary>neo4j | memory</summary>
    public string Graph { get; set; } = "memory";
    /// <summary>ollama | replay</summary>
    public string Chat { get; set; } = "ollama";
    /// <summary>onnx | hash — "hash" is a deterministic dev stand-in used when ONNX models are absent.</summary>
    public string Embedder { get; set; } = "onnx";
    /// <summary>onnx | lexical</summary>
    public string Reranker { get; set; } = "onnx";
    /// <summary>When true, fall back to the local provider if the hosted one fails its startup probe.</summary>
    public bool FallbackToLocal { get; set; } = true;
}

public sealed class StorageOptions
{
    public string DataDirectory { get; set; } = "data";
    public string SqliteFile { get; set; } = "ragladder.db";
    public string CorpusDirectory { get; set; } = "corpus";
    public string RecordingsDirectory { get; set; } = "recordings";

    /// <summary>
    /// Which PDF in corpus/demo the Load-demo button attaches. Defaults to the Spider-Man seed,
    /// which is a subset of the full dossier chosen so a complete processing pass finishes in
    /// minutes on a CPU-only machine. Set it to serendib-dossier.pdf for the full corpus.
    /// </summary>
    public string DemoPdf { get; set; } = "spiderman-seed.pdf";
    public string SqlitePath => Path.Combine(DataDirectory, SqliteFile);
}

public sealed class EmbeddingOptions
{
    public string ModelId { get; set; } = "all-MiniLM-L6-v2";
    public string ModelPath { get; set; } = "models/all-MiniLM-L6-v2/model.onnx";
    public string VocabPath { get; set; } = "models/all-MiniLM-L6-v2/vocab.txt";
    /// <summary>Model tag used when Providers:Embedder is "ollama". Width is detected at runtime.</summary>
    public string OllamaModel { get; set; } = "nomic-embed-text";
    /// <summary>Expected width. Informational only — collections are created from the actual width.</summary>
    public int Dimensions { get; set; } = 384;
    public int MaxTokens { get; set; } = 256;
    public int BatchSize { get; set; } = 32;
}

public sealed class RerankOptions
{
    public string ModelId { get; set; } = "ms-marco-MiniLM-L-6-v2";
    public string ModelPath { get; set; } = "models/ms-marco-MiniLM-L-6-v2/model.onnx";
    public string VocabPath { get; set; } = "models/ms-marco-MiniLM-L-6-v2/vocab.txt";
    public int MaxTokens { get; set; } = 320;
}

public sealed class OllamaOptions
{
    public string BaseUrl { get; set; } = "https://ollama.com";
    public string ApiKey { get; set; } = "";
    /// <summary>Model used to answer questions.</summary>
    public string ChatModel { get; set; } = "gpt-oss:120b-cloud";
    /// <summary>Separately configurable: extraction needs stronger instruction-following and reliable JSON (spec §4.2).</summary>
    public string ExtractionModel { get; set; } = "gpt-oss:120b-cloud";
    public int TimeoutSeconds { get; set; } = 600;
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Concurrent chat calls. The spec caps this at two; one is faster against a local model and
    /// still complies.
    ///
    /// The reason is prompt prefix caching. Extraction sends an identical ~2,500-token system
    /// prompt — ontology, direction table, worked examples — on every chunk, and only the chunk
    /// text differs. Served sequentially, the shared prefix stays in the KV cache and prefill
    /// drops from 131 s to 2–4 s (measured). Run two streams concurrently and they evict each
    /// other's cache slot, so both pay full prefill every time.
    ///
    /// Raise it to 2 for a hosted provider, where the round trip dominates and cache locality
    /// does not apply.
    /// </summary>
    public int MaxConcurrency { get; set; } = 1;
    /// <summary>Validate the configured tags against /api/tags at startup (spec §4.2).</summary>
    public bool ValidateTagsAtStartup { get; set; } = true;

    /// <summary>
    /// Context window for local models. Ollama defaults to 4096, and the extraction prompt —
    /// ontology, direction table and four worked examples — runs close to 3,000 tokens before the
    /// chunk is added, leaving no room for the response. Anything over the window is silently
    /// dropped, so this is set explicitly. Ignored by Ollama Cloud. 0 leaves the server default.
    /// </summary>
    public int NumCtx { get; set; } = 8192;
}

/// <summary>
/// Any endpoint speaking the OpenAI chat-completions shape: OpenAI, Azure OpenAI, an internal
/// gateway, LiteLLM, vLLM, LM Studio, llama.cpp's server. This is the escape hatch for
/// organisations that block ollama.com but run a sanctioned LLM endpoint of their own.
/// </summary>
public sealed class OpenAiCompatibleOptions
{
    /// <summary>e.g. https://api.openai.com/v1, or https://{resource}.openai.azure.com</summary>
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ChatModel { get; set; } = "gpt-4o-mini";
    public string ExtractionModel { get; set; } = "gpt-4o-mini";

    /// <summary>
    /// Path appended to BaseUrl. <c>{model}</c> is substituted, which is what Azure OpenAI needs:
    /// "openai/deployments/{model}/chat/completions?api-version=2024-10-21".
    /// </summary>
    public string ChatPath { get; set; } = "chat/completions";

    /// <summary>"Authorization" for OpenAI-style, "api-key" for Azure OpenAI.</summary>
    public string AuthHeader { get; set; } = "Authorization";
    /// <summary>"Bearer" for OpenAI-style; leave empty for Azure's raw api-key header.</summary>
    public string AuthScheme { get; set; } = "Bearer";

    /// <summary>Send response_format=json_object. Turn off for gateways that reject it.</summary>
    public bool SupportsJsonMode { get; set; } = true;
    /// <summary>Some reasoning models reject an explicit temperature.</summary>
    public bool SendTemperature { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 180;
    public int MaxRetries { get; set; } = 3;
    public int MaxConcurrency { get; set; } = 2;
}

public sealed class QdrantOptions
{
    public string Url { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 60;
}

public sealed class Neo4jOptions
{
    public string Uri { get; set; } = "";
    public string User { get; set; } = "neo4j";
    public string Password { get; set; } = "";
    public string Database { get; set; } = "neo4j";
}

public sealed class ChunkingOptions
{
    public int FixedTokens { get; set; } = 400;
    public int RecursiveTokens { get; set; } = 400;
    public int RecursiveOverlapTokens { get; set; } = 80;
    public int CommitBatchSize { get; set; } = 500;
}

public sealed class ExtractionOptions
{
    /// <summary>quick | thorough. Quick skips the verification pass (spec §4.1).</summary>
    public string DefaultMode { get; set; } = "thorough";
    public int ChunkCap { get; set; } = 120;
    public int VerificationBatchSize { get; set; } = 10;
    public double ConfidenceFloor { get; set; } = 0.6;
    public double PartialConfidenceMultiplier { get; set; } = 0.7;
    /// <summary>Which chunk strategy the knowledge graph is extracted from.</summary>
    public string SourceStrategy { get; set; } = "recursive";
    public int PreviousChunkTailChars { get; set; } = 200;
    public string OntologyVersion { get; set; } = "film-v3";
}

public sealed class RetrievalOptions
{
    public int TopK { get; set; } = 5;
    public int CandidateK { get; set; } = 50;
    public int RrfK { get; set; } = 60;
    public double MinEdgeConfidence { get; set; } = 0.6;
    public int MaxPathHops { get; set; } = 6;
    public string RefusalText { get; set; } = "Not found in the provided documents.";
}

public sealed class AgenticOptions
{
    public int MaxIterations { get; set; } = 4;
    public int MaxChatCalls { get; set; } = 6;
}

/// <summary>Spec §10 — domain knobs for the film ontology and entity resolution.</summary>
public sealed class DomainOptions
{
    public string OntologyPath { get; set; } = "config/film-ontology.json";
    public string DiminutivesPath { get; set; } = "config/name-diminutives.json";
    public string[] StudioSuffixes { get; set; } =
        ["Pictures", "Studios", "Entertainment", "Films", "Productions", "Inc", "Ltd"];
    public string[] TitleArticles { get; set; } = ["The", "A", "An"];
    public double EntityMergeCosine { get; set; } = 0.92;
    public double EntityMergeJaroWinkler { get; set; } = 0.88;
    public int MaxPathHops { get; set; } = 12;
    public bool BlockCrossTypeMerge { get; set; } = true;
}

public sealed class ReplayOptions
{
    /// <summary>Enabled by the --replay command line switch (spec §12).</summary>
    public bool Enabled { get; set; }
    /// <summary>Record every live chat call into the recordings directory.</summary>
    public bool Record { get; set; }
}
