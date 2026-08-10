using System.Text;
using System.Text.Json;
using RagLadder.Api.Llm;

namespace RagLadder.Api.Reranking;

/// <summary>
/// Reranking by asking the chat model to score each (query, passage) pair, for networks where the
/// ms-marco cross-encoder cannot be downloaded.
///
/// It is a genuine reranker, not a stand-in: the model reads the query and the passage together,
/// which is exactly the property that lets stage 5 pull a credit buried deep in a crew list up to
/// rank 1 when cosine similarity cannot. The cost is one model call per batch of passages instead
/// of a free in-process pass, so it is slower and not free — prefer the ONNX cross-encoder when
/// you can obtain it.
///
/// Falls back to the lexical scorer if the model call fails, so a rate limit degrades the rung
/// rather than breaking the question.
/// </summary>
public sealed class LlmReranker(IChatClient chat, ILogger<LlmReranker> log) : IReranker
{
    private const int BatchSize = 10;
    private const int PassageChars = 900;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly LexicalReranker _fallback = new();

    public string ModelId => "llm-reranker";
    public bool IsRealModel => true;

    public async Task<IReadOnlyList<double>> ScoreAsync(string query, IReadOnlyList<string> passages, CancellationToken ct = default)
    {
        if (passages.Count == 0) return [];

        var scores = new double[passages.Count];
        var scored = new bool[passages.Count];

        for (var offset = 0; offset < passages.Count; offset += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = passages.Skip(offset).Take(BatchSize).ToArray();

            var response = await chat.CompleteAsync(new ChatRequest
            {
                Model = chat.ChatModel,
                Messages = [ChatMessage.System(SystemPrompt), ChatMessage.User(BuildUser(query, batch))],
                Temperature = 0,
                JsonOnly = true,
                Purpose = ChatPurpose.Rerank,
                CacheScope = $"batch{offset}",
            }, ct);

            if (response.Failed)
            {
                log.LogWarning("LLM rerank batch failed ({Message}); falling back to lexical scoring for it.", response.Warning);
                continue;
            }

            var parsed = JsonText.TryDeserialize<ScoreEnvelope>(response.Content, Json);
            if (parsed?.Scores is null)
            {
                log.LogWarning("LLM rerank returned malformed JSON; falling back to lexical scoring for that batch.");
                continue;
            }

            foreach (var row in parsed.Scores)
            {
                if (row.Index < 0 || row.Index >= batch.Length) continue;
                scores[offset + row.Index] = Math.Clamp(row.Score, 0, 1);
                scored[offset + row.Index] = true;
            }
        }

        // Anything the model did not score keeps a lexical score, compressed below 1 so a
        // confidently-ranked passage always outranks a fallback-scored one.
        if (Array.Exists(scored, s => !s))
        {
            var lexical = await _fallback.ScoreAsync(query, passages, ct);
            var max = lexical.Count == 0 ? 0 : lexical.Max();
            for (var i = 0; i < scores.Length; i++)
                if (!scored[i])
                    scores[i] = max > 0 ? lexical[i] / max * 0.5 : 0;
        }

        return scores;
    }

    private const string SystemPrompt = """
        You score how well each passage answers a query. You return JSON and nothing else.

        Score each passage from 0.0 to 1.0:
          1.0  the passage directly and completely answers the query
          0.7  the passage contains the answer among other material
          0.4  the passage is about the right subject but does not answer the query
          0.0  the passage is unrelated

        Judge only whether the passage answers THIS query. A passage can be long, well written and
        entirely about the right film and still score 0.4 if the specific fact asked for is absent.
        Conversely a passage that mentions the answer once, buried in a list, scores high.

        Return every index you were given, in any order:
        {"scores":[{"index":0,"score":0.9},{"index":1,"score":0.2}]}
        """;

    private static string BuildUser(string query, string[] passages)
    {
        var sb = new StringBuilder();
        sb.AppendLine("QUERY:");
        sb.AppendLine(query);
        sb.AppendLine();
        sb.AppendLine("PASSAGES:");
        for (var i = 0; i < passages.Length; i++)
        {
            var text = passages[i].Length <= PassageChars ? passages[i] : passages[i][..PassageChars] + "…";
            sb.AppendLine($"[{i}] {text.ReplaceLineEndings(" ")}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private sealed class ScoreEnvelope
    {
        public List<ScoreRow>? Scores { get; set; }
    }

    private sealed class ScoreRow
    {
        public int Index { get; set; }
        public double Score { get; set; }
    }
}
