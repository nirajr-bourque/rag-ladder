using System.Text.Json;
using RagLadder.Api.Llm;
using RagLadder.Api.Models;

namespace RagLadder.Api.Ask;

/// <summary>
/// Stage 6. Users do not write like press kits: they ask "who did the music", the document says
/// "original score composed by". The glossary is domain-tuned because that gap is the whole
/// lesson of this rung (spec §7.5).
/// </summary>
public sealed class QueryRewriter(IChatClient chat)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string Glossary = """
        GLOSSARY — the phrasing a viewer uses, and the phrasing a press kit uses:
          music / soundtrack / theme      -> original score, composed by, composer
          filmed by / shot by / camera    -> cinematographer, director of photography
          the guy who made it / made by   -> directed by, director
          made money / earnings / took    -> box office gross, opening weekend, domestic, worldwide
          starred / was in / played in    -> cast, credited as, principal role
          the studio behind it            -> produced by, distributed by, production company
          episode where / the one where   -> episode guide, synopsis
          won / picked up                 -> award category, nominated for, ceremony
          writer / script                 -> screenplay by, story by
        """;

    public async Task<(RewriteBlock Block, long ElapsedMs, int Calls, string? Warning)> RewriteAsync(
        string question, CancellationToken ct)
    {
        var response = await chat.CompleteAsync(new ChatRequest
        {
            Model = chat.ChatModel,
            Messages =
            [
                ChatMessage.System(
                    "Rewrite the user's question into the vocabulary a film press kit would use, so that it " +
                    "retrieves better against such documents. Keep every proper noun, title, figure and year " +
                    "exactly as written — never normalise or correct them. Add the domain terms that the " +
                    "document is likely to use.\n\n" + Glossary +
                    "\n\nReturn JSON only: {\"rewritten\": \"...\", \"keywords\": [\"...\"]}"),
                ChatMessage.User(question)
            ],
            Temperature = 0,
            JsonOnly = true,
            Purpose = ChatPurpose.QueryRewrite,
        }, ct);

        if (response.Failed)
            return (new RewriteBlock { Original = question, Rewritten = question }, response.ElapsedMs, 0,
                $"Query rewrite failed: {response.Warning}. Using the original question.");

        var parsed = JsonText.TryDeserialize<RewritePayload>(response.Content, Json);
        var rewritten = string.IsNullOrWhiteSpace(parsed?.Rewritten) ? question : parsed!.Rewritten!.Trim();

        return (new RewriteBlock
        {
            Original = question,
            Rewritten = rewritten,
            Keywords = parsed?.Keywords ?? [],
        }, response.ElapsedMs, response.FromCache ? 0 : 1, null);
    }

    private sealed class RewritePayload
    {
        public string? Rewritten { get; set; }
        public List<string>? Keywords { get; set; }
    }
}
