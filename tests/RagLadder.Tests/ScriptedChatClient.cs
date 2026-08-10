using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RagLadder.Api.Llm;

namespace RagLadder.Tests;

/// <summary>
/// A deterministic stand-in for the model, so the whole pipeline — extraction, resolution,
/// commit, traversal, aggregation — can be exercised without a network call or an API key.
///
/// The extraction branch is a rule-based reader of the corpus's own credit formatting
/// ("**Director** X", "X as Y", "Original score composed by Z"). It emits the same JSON shape the
/// real prompt asks for, with verbatim evidence spans, so the grounding filter is genuinely
/// exercised rather than bypassed.
/// </summary>
public sealed partial class ScriptedChatClient : IChatClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Kind => "scripted";
    public string ChatModel => "scripted-chat";
    public string ExtractionModel => "scripted-extraction";
    public int LiveCallCount => Calls;
    public int Calls { get; private set; }

    public Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        Calls++;
        var user = request.Messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
        var content = request.Purpose switch
        {
            ChatPurpose.Extraction => Extract(ChunkOf(user)),
            ChatPurpose.Verification => Verify(user),
            ChatPurpose.SectionSummary => Summarize(user),
            ChatPurpose.QueryRewrite => """{"rewritten":"REWRITTEN","keywords":[]}""",
            ChatPurpose.Routing => """{"classification":"lookup","rationale":"scripted"}""",
            ChatPurpose.Agentic => """{"action":"answer","thought":"scripted planner answers immediately"}""",
            ChatPurpose.PathEndpoints => """{"from":null,"to":null}""",
            ChatPurpose.Unconstrained => "Scripted unconstrained answer about a film the model believes it knows.",
            ChatPurpose.GoldenGeneration => """{"questions":[]}""",
            _ => Answer(user),
        };
        return Task.FromResult(new ChatResult { Content = content, Model = request.Model });
    }

    public Task<ProviderHealth> HealthAsync(CancellationToken ct = default) =>
        Task.FromResult(new ProviderHealth("chat", ProviderHealth.Ok, "Scripted test client."));

    // ----- answer ---------------------------------------------------------

    /// <summary>
    /// Echoes the retrieved context so a test can assert what reached the model, and honours the
    /// refusal contract when nothing was retrieved.
    /// </summary>
    private static string Answer(string user)
    {
        var start = user.IndexOf("CONTEXT", StringComparison.Ordinal);
        var end = user.IndexOf("QUESTION", StringComparison.Ordinal);
        if (start < 0 || end < 0) return "Not found in the provided documents.";

        var context = user[start..end];
        if (context.Contains("(no context was retrieved)", StringComparison.Ordinal))
            return "Not found in the provided documents.";

        var body = context.Length > 1200 ? context[..1200] : context;
        return "ANSWER-FROM-CONTEXT: " + body.ReplaceLineEndings(" ");
    }

    private static string Summarize(string user)
    {
        var heading = user.Split('\n').FirstOrDefault(l => l.StartsWith("Heading:", StringComparison.Ordinal)) ?? "Section";
        return heading.Replace("Heading:", "Summary of").Trim();
    }

    private static string Verify(string user)
    {
        var count = Regex.Matches(user, @"^\s*\[(\d+)\]", RegexOptions.Multiline).Count;
        var verdicts = Enumerable.Range(0, count)
            .Select(i => new { index = i, verdict = "SUPPORTED", reason = "scripted" });
        return JsonSerializer.Serialize(new { verdicts }, Json);
    }

    // ----- extraction -----------------------------------------------------

    private static string ChunkOf(string user)
    {
        var marker = user.IndexOf("CHUNK — extract from exactly this text:", StringComparison.Ordinal);
        if (marker < 0) return "";
        var body = user[marker..];
        var first = body.IndexOf("---", StringComparison.Ordinal);
        var last = body.LastIndexOf("---", StringComparison.Ordinal);
        return first < 0 || last <= first ? "" : body[(first + 3)..last].Trim();
    }

    [GeneratedRegex(@"(?<person>[A-Z][\w'’.-]+(?: [A-Z][\w'’.-]+){0,3}) as (?<character>[A-Z][\w'’.-]+(?: [A-Z][\w'’.-]+){0,3})")]
    private static partial Regex CastCredit();

    [GeneratedRegex(@"Original score composed by (?<person>[A-Z][\w'’.-]+(?: [A-Z][\w'’.-]+){0,3})")]
    private static partial Regex ComposerCredit();

    [GeneratedRegex(@"Directors? (?<people>[A-Z][\w'’.-]+(?: [A-Z][\w'’.-]+){0,3}(?:, [A-Z][\w'’.-]+(?: [A-Z][\w'’.-]+){0,3})*)")]
    private static partial Regex DirectorCredit();

    [GeneratedRegex(@"Director of photography (?<person>[A-Z][\w'’.-]+(?: [A-Z][\w'’.-]+){0,3})")]
    private static partial Regex CinematographerCredit();

    [GeneratedRegex(@"### Section \d+ — (?<title>[^(\n]+?) \((?<year>\d{4})\)")]
    private static partial Regex SectionTitle();

    [GeneratedRegex(@"^(?<title>[A-Z][^\n]{2,60}?) \((?<year>(19|20)\d{2})\)", RegexOptions.Multiline)]
    private static partial Regex TitleWithYear();

    private static string Extract(string chunk)
    {
        var entities = new List<object>();
        var relations = new List<object>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddEntity(string name, string type, string evidence, int? year = null)
        {
            var key = type + "|" + name;
            if (!seen.Add(key)) return;
            entities.Add(year is null
                ? new { name, type, evidence }
                : (object)new { name, type, evidence, year });
        }

        // The work this chunk is about, so credits have somewhere to attach.
        string? workTitle = null;
        int? workYear = null;
        var titleMatch = SectionTitle().Match(chunk);
        if (!titleMatch.Success) titleMatch = TitleWithYear().Match(chunk);
        if (titleMatch.Success)
        {
            workTitle = titleMatch.Groups["title"].Value.Trim();
            workYear = int.Parse(titleMatch.Groups["year"].Value);
            AddEntity(workTitle, "Film", titleMatch.Value, workYear);
        }

        foreach (Match m in CastCredit().Matches(chunk))
        {
            var person = m.Groups["person"].Value.Trim();
            var character = m.Groups["character"].Value.Trim();
            if (person.Length < 4 || character.Length < 3) continue;

            AddEntity(person, "Person", m.Value);
            AddEntity(character, "Character", m.Value);
            relations.Add(new { subject = person, predicate = "PLAYED", @object = character, evidence = m.Value, confidence = 0.95 });
            if (workTitle is not null)
                relations.Add(new { subject = person, predicate = "ACTED_IN", @object = workTitle, evidence = m.Value, confidence = 0.85 });
        }

        foreach (Match m in ComposerCredit().Matches(chunk))
        {
            var person = m.Groups["person"].Value.Trim();
            AddEntity(person, "Person", m.Value);
            if (workTitle is not null)
                relations.Add(new { subject = person, predicate = "COMPOSED_FOR", @object = workTitle, evidence = m.Value, confidence = 0.9 });
        }

        foreach (Match m in DirectorCredit().Matches(chunk))
        {
            foreach (var person in m.Groups["people"].Value.Split(", ", StringSplitOptions.RemoveEmptyEntries))
            {
                var name = person.Trim();
                if (name.Length < 4) continue;
                AddEntity(name, "Person", m.Value);
                if (workTitle is not null)
                    relations.Add(new { subject = name, predicate = "DIRECTED", @object = workTitle, evidence = m.Value, confidence = 0.92 });
            }
        }

        foreach (Match m in CinematographerCredit().Matches(chunk))
        {
            var person = m.Groups["person"].Value.Trim();
            AddEntity(person, "Person", m.Value);
            if (workTitle is not null)
                // Deliberately emitted the wrong way round: the direction filter must flip it.
                relations.Add(new { subject = person, predicate = "SHOT_BY", @object = workTitle, evidence = m.Value, confidence = 0.9 });
        }

        return JsonSerializer.Serialize(new { entities, relations }, Json);
    }

    public static string Describe() =>
        new StringBuilder()
            .AppendLine("Scripted chat client: rule-based extraction over the corpus credit format.")
            .ToString();
}
