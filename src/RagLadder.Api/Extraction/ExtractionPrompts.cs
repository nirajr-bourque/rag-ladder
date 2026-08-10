using System.Text;
using RagLadder.Api.Models;

namespace RagLadder.Api.Extraction;

/// <summary>
/// The extraction prompt (spec §6.5). Every instruction here earns its place:
/// the verbatim-evidence rule is the highest-leverage line in the whole system, the direction
/// table fixes the most common fault in this domain, and the negative example is what stops the
/// model inventing plausible collaborations that the document never states.
/// </summary>
public static class ExtractionPrompts
{
    public static string System(Ontology ontology)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You extract a knowledge graph from film and television documents. You return JSON and nothing else.");
        sb.AppendLine();
        sb.AppendLine("PERMITTED NODE TYPES — use no others, and never invent a type:");
        foreach (var n in ontology.NodeTypes)
            sb.AppendLine($"  {n.Name}{(n.Notes is null ? "" : "   // " + n.Notes)}");
        sb.AppendLine();
        // The predicate column is written bare, with no arrows. An earlier version of this prompt
        // drew relations as "(Person) -DIRECTED-> (Film)"; a 3B model copied that decoration into
        // the predicate field on 49 of 90 relations, and every one was dropped as non-conformant.
        // Never show the model a decorated form of a value you are asking it to emit.
        sb.AppendLine("PERMITTED RELATIONS. The predicate column is exactly what goes in the");
        sb.AppendLine("\"predicate\" field — copy it verbatim, with no arrows or punctuation added:");
        sb.AppendLine();
        sb.AppendLine("  subject type          predicate          object type");
        sb.AppendLine("  --------------------  -----------------  --------------------");
        foreach (var r in ontology.RelationTypes)
        {
            var props = r.Properties.Length == 0 ? "" : $"   properties: {string.Join(", ", r.Properties)}";
            sb.AppendLine($"  {string.Join('|', r.From),-20}  {r.Predicate,-17}  {string.Join('|', r.To),-20}{props}");
        }
        sb.AppendLine();
        sb.AppendLine("DIRECTION — get these the right way round; inverted crew credits are the most");
        sb.AppendLine("common mistake in this domain. Read each line as subject, predicate, object:");
        sb.AppendLine("  Person  DIRECTED      Film      (a Film is never the subject of DIRECTED)");
        sb.AppendLine("  Person  WROTE         Film");
        sb.AppendLine("  Person  COMPOSED_FOR  Film");
        sb.AppendLine("  Person  ACTED_IN      Film");
        sb.AppendLine("  Film    SHOT_BY       Person    (cinematographer; the Film is the subject)");
        sb.AppendLine("  Film    EDITED_BY     Person");
        sb.AppendLine("  Film    PRODUCED_BY   Studio");
        sb.AppendLine();
        sb.AppendLine("RULES");
        sb.AppendLine("1. EVIDENCE MUST BE A VERBATIM SUBSTRING OF THE CHUNK. Copy the characters exactly from");
        sb.AppendLine("   the chunk text. Do not paraphrase, do not correct spelling, do not add or remove words.");
        sb.AppendLine("   To repeat, because it matters more than anything else in these instructions: the");
        sb.AppendLine("   `evidence` value must appear character-for-character inside the chunk.");
        sb.AppendLine("2. A performance always yields BOTH entities. \"X plays Y\" gives a Person X, a Character Y,");
        sb.AppendLine("   and a PLAYED relation. Never collapse a performer and a role into one node, even when");
        sb.AppendLine("   they share a name.");
        sb.AppendLine("3. NEVER INFER A CREDIT. If a person is named without a stated role, emit the Person node");
        sb.AppendLine("   and no relation. Two people appearing in the same paragraph is not a collaboration.");
        sb.AppendLine("4. Films carry their year whenever the chunk states one — remakes share titles.");
        sb.AppendLine("5. Both `subject` and `object` of every relation must also appear in the `entities` array.");
        sb.AppendLine($"6. Use {Ontology.Fallback} only when nothing else fits. It is a last resort, not a default.");
        sb.AppendLine("7. Set `confidence` to how strongly the chunk states the fact: 0.9+ for an explicit credit");
        sb.AppendLine("   line, 0.7 for clear prose, 0.5 or below for anything hedged or implied.");
        sb.AppendLine();
        sb.AppendLine("OUTPUT — this exact shape, no prose, no code fences:");
        sb.AppendLine("""
            {
              "entities": [
                { "name": "Ilse Vantor", "type": "Person", "evidence": "Ilse Vantor returns as Commander Reyes" },
                { "name": "Commander Reyes", "type": "Character", "evidence": "Ilse Vantor returns as Commander Reyes" }
              ],
              "relations": [
                { "subject": "Ilse Vantor", "predicate": "PLAYED", "object": "Commander Reyes",
                  "evidence": "Ilse Vantor returns as Commander Reyes", "confidence": 0.95, "properties": {} }
              ]
            }
            """);
        return sb.ToString();
    }

    /// <summary>Four worked examples: a cast list, a crew list, prose, and one negative.</summary>
    public static string FewShot() => """
        EXAMPLE 1 — cast list.
        Chunk: "Cast: Ilse Vantor (Commander Reyes), Dara Okonjo (Pilot Adeyemi). The Vermilion Hour (2024)."
        Output:
        {"entities":[
          {"name":"Ilse Vantor","type":"Person","evidence":"Ilse Vantor (Commander Reyes)"},
          {"name":"Commander Reyes","type":"Character","evidence":"Ilse Vantor (Commander Reyes)"},
          {"name":"Dara Okonjo","type":"Person","evidence":"Dara Okonjo (Pilot Adeyemi)"},
          {"name":"Pilot Adeyemi","type":"Character","evidence":"Dara Okonjo (Pilot Adeyemi)"},
          {"name":"The Vermilion Hour","type":"Film","year":2024,"evidence":"The Vermilion Hour (2024)"}],
         "relations":[
          {"subject":"Ilse Vantor","predicate":"PLAYED","object":"Commander Reyes","evidence":"Ilse Vantor (Commander Reyes)","confidence":0.95,"properties":{}},
          {"subject":"Ilse Vantor","predicate":"ACTED_IN","object":"The Vermilion Hour","evidence":"Ilse Vantor (Commander Reyes)","confidence":0.85,"properties":{"billing":"1"}},
          {"subject":"Dara Okonjo","predicate":"PLAYED","object":"Pilot Adeyemi","evidence":"Dara Okonjo (Pilot Adeyemi)","confidence":0.95,"properties":{}}]}

        EXAMPLE 2 — crew list. Note the direction of the cinematography and editing credits.
        Chunk: "Directed by Piet Hansen. Director of photography Ana Lindqvist. Edited by Tomas Reis. Original score composed by Mira Fontaine."
        Output:
        {"entities":[
          {"name":"Piet Hansen","type":"Person","evidence":"Directed by Piet Hansen"},
          {"name":"Ana Lindqvist","type":"Person","evidence":"Director of photography Ana Lindqvist"},
          {"name":"Tomas Reis","type":"Person","evidence":"Edited by Tomas Reis"},
          {"name":"Mira Fontaine","type":"Person","evidence":"Original score composed by Mira Fontaine"}],
         "relations":[]}
        (No relations: the chunk names the crew but never names the work they worked on. Do not
        guess the title from surrounding context you were not given.)

        EXAMPLE 3 — prose.
        Chunk: "Halcyon Films was acquired by Meridian Pictures in 2019, consolidating both catalogues."
        Output:
        {"entities":[
          {"name":"Meridian Pictures","type":"Studio","evidence":"acquired by Meridian Pictures in 2019"},
          {"name":"Halcyon Films","type":"Studio","evidence":"Halcyon Films was acquired"}],
         "relations":[
          {"subject":"Meridian Pictures","predicate":"ACQUIRED","object":"Halcyon Films","evidence":"Halcyon Films was acquired by Meridian Pictures in 2019","confidence":0.9,"properties":{"year":"2019"}}]}

        EXAMPLE 4 — NEGATIVE. A plausible but unstated collaboration must be omitted.
        Chunk: "The festival programme paired The Vermilion Hour with Nightjar. Ilse Vantor and Piet Hansen both attended the screening."
        Output:
        {"entities":[
          {"name":"The Vermilion Hour","type":"Film","evidence":"The Vermilion Hour"},
          {"name":"Nightjar","type":"Film","evidence":"Nightjar"},
          {"name":"Ilse Vantor","type":"Person","evidence":"Ilse Vantor and Piet Hansen both attended"},
          {"name":"Piet Hansen","type":"Person","evidence":"Ilse Vantor and Piet Hansen both attended"}],
         "relations":[]}
        (Attending the same screening is not a credit. Emitting ACTED_IN or DIRECTED here would be
        a fabrication, however likely it seems.)
        """;

    public static string User(
        string chunkText,
        FrontMatter frontMatter,
        string? sectionSummary,
        string? previousChunkTail)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SECTION CONTEXT");
        sb.AppendLine($"  subject : {frontMatter.Subject ?? "(unknown)"}");
        sb.AppendLine($"  year    : {frontMatter.Year?.ToString() ?? "(unknown)"}");
        sb.AppendLine($"  docType : {frontMatter.DocType ?? "(unknown)"}");
        sb.AppendLine($"  studio  : {frontMatter.Studio ?? "(unknown)"}");
        if (!string.IsNullOrWhiteSpace(sectionSummary))
            sb.AppendLine($"  summary : {sectionSummary.Trim()}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(previousChunkTail))
        {
            sb.AppendLine("TAIL OF THE PREVIOUS CHUNK — for resolving pronouns and continued lists only.");
            sb.AppendLine("Do NOT extract evidence from it; evidence must come from the chunk below.");
            sb.AppendLine($"  …{previousChunkTail.Trim()}");
            sb.AppendLine();
        }

        sb.AppendLine("CHUNK — extract from exactly this text:");
        sb.AppendLine("---");
        sb.AppendLine(chunkText);
        sb.AppendLine("---");
        return sb.ToString();
    }

    /// <summary>
    /// The verification pass is framed as a judge, not an extractor, and batched ten triples per
    /// call to keep the call count near 1.3 per chunk (spec §4.1, §6.6).
    /// </summary>
    public static string VerificationSystem() => """
        You are a strict fact-checking judge. You are given a source chunk and a list of candidate
        triples that were extracted from it. For each triple, decide whether the chunk actually
        supports it.

        Verdicts:
          SUPPORTED   — the chunk states this, explicitly and in this direction.
          PARTIAL     — the chunk gestures at it but the claim overreaches: the direction is
                        uncertain, the role is assumed, or the link is implied rather than stated.
          UNSUPPORTED — the chunk does not state this. Includes correct-sounding facts that simply
                        are not in the text, and relations whose direction is reversed.

        You are the last line of defence against a plausible fabrication reaching the graph. When
        the chunk does not clearly state the claim, the verdict is UNSUPPORTED.

        Return JSON only:
        {"verdicts":[{"index":0,"verdict":"SUPPORTED","reason":"short reason"}]}
        """;

    public static string VerificationUser(string chunkText, IReadOnlyList<ProposedRelation> batch)
    {
        var sb = new StringBuilder();
        sb.AppendLine("CHUNK:");
        sb.AppendLine("---");
        sb.AppendLine(chunkText);
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("CANDIDATE TRIPLES:");
        for (var i = 0; i < batch.Count; i++)
        {
            var r = batch[i];
            sb.AppendLine($"  [{i}] ({r.SubjectType}) {r.SubjectName} -{r.Predicate}-> ({r.ObjectType}) {r.ObjectName}");
            sb.AppendLine($"      claimed evidence: \"{r.Evidence}\"");
        }
        return sb.ToString();
    }

    /// <summary>One short paragraph per section, prepended to contextual chunks (spec §5 step 5).</summary>
    public static string SectionSummarySystem() =>
        "Summarise the section in one sentence of at most 30 words. Name the film, series or person " +
        "it concerns and say what kind of document it is. State only what the text says. No preamble.";
}
