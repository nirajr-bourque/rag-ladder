# RAG Ladder — Build Spec v3 (Film & Television domain)

Upload a film-industry PDF, process it into vectors and an LLM-extracted knowledge graph,
then step through a RAG pipeline one feature at a time and watch answers change at each rung.

**Audience:** engineers, mostly unfamiliar with RAG internals.
**Format:** live demo. Everything asserted on a slide must be reproducible by clicking
something in the UI.

**Changes from v2:** retargeted from a generic corpus to film and television. Adds a domain
ontology, domain-specific entity resolution, path/collaboration queries as a first-class
stage-10 mode, and a revised trap set.

---

## 0. Why this domain suits the demo

Worth saying out loud in the talk. Film data is the best possible teaching case for graph RAG
because the audience already has intuitions about it:

- **Six degrees of separation is a graph query.** "How is actor A connected to actor B" is
  something everyone understands, and vector search cannot answer it at any `k`. This is the
  single most convincing thirty seconds in the demo.
- **Collaboration questions are multi-hop.** "Which cinematographer has shot more than one
  film for this director" requires two traversals and no amount of similarity.
- **Name collisions are everywhere.** Actor vs. character, remakes sharing a title, two people
  with the same name. Entity resolution stops being an abstraction.
- **Aggregation is natural.** "Which studio released the most films in 2024" is unanswerable
  from top-k, trivially answerable in Cypher.
- **Exact strings matter.** Box office figures, production codes, award categories. Embeddings
  are famously poor at these, which makes the hybrid-search rung land hard.

---

## 1. Guiding principles

1. **Every rung is independently runnable.** The same question at stage 4 and stage 5 must be
   viewable side by side.
2. **Answers come only from the selected flow.** No cross-stage caching, no parametric
   fallback. If the pipeline didn't retrieve it, the model must not say it.
3. **Show the work.** Retrieved chunks, scores, rank deltas, extracted triples, evidence spans,
   traversal paths, timings and the assembled prompt are all visible.
4. **Extraction is verified, not trusted.** Every LLM triple carries an evidence span, a
   verification verdict, and a confidence score. Unverifiable triples are dropped.
5. **Deterministic where possible, LLM where necessary.** Structural edges are code. Semantic
   edges are LLM. Never the reverse.
6. **Cheap to re-run.** Embeddings and extractions cached by content hash. Reprocessing an
   unchanged document costs zero model calls.

---

## 2. Legal and data constraints — read first

This domain carries real copyright and personality-rights exposure. The rules are simple:

- **Never ingest screenplays, scripts, shooting drafts, or subtitle files.**
- **Never ingest published reviews, articles or trade-press prose.** Even for internal use,
  a demo that visibly reproduces copyrighted criticism is a bad look in front of colleagues.
- **The primary corpus is entirely fictional.** Invented studio, invented films, invented
  people. This also lets you plant traps precisely, which real data would never allow.
- **If you demo real data**, restrict it to factual metadata — titles, dates, credits, box
  office — from Wikidata (CC0) or the IMDb non-commercial datasets, and never free prose about
  real named people. Attribute the source in the UI.
- Do not generate synthetic prose *about real named people*. Fictional people only.

---

## 3. Corpus

### 3.1 The universe — Meridian Pictures

A fictional mid-size studio. Everything below is invented. Commit as
`corpus/demo/meridian-press-archive.pdf` (~35 pages), plus the caches, so the demo starts
instantly with zero model calls.

Scale it to be small enough to reason about and large enough for real traversals:

| Entity | Count | Notes |
|---|---|---|
| Films | 14 | Across 3 franchises + 5 standalone |
| TV series | 3 | With seasons and selected episodes |
| People | ~45 | Actors, directors, writers, composers, cinematographers, producers |
| Characters | ~30 | Deliberately overlapping names with actors — see §6.4 |
| Studios / production companies | 5 | Including one that was acquired mid-timeline |
| Awards | 2 ceremonies × 6 categories | Across 4 years |
| Franchises | 3 | One with a remake, one with a spin-off series |

Design the universe so the graph has genuine depth: at least one actor appearing in three
franchises, one director-cinematographer pair recurring across four films, and at least one
six-hop path between two people who never worked together directly.

### 3.2 Document sections in the PDF

| Section | Purpose in the demo |
|---|---|
| Studio overview and history | Long prose, entity-dense |
| Press kits (one per film) | Cast/crew lists, synopsis, production notes |
| Casting announcements | Short, dated, superseded by later ones |
| Box office reports | Exact numbers — the hybrid-search rung |
| Awards ceremony results | Aggregation targets |
| Series bibles and episode guides | Orphan-context and boundary traps |
| Talent biographies | Filmographies that split across page breaks |
| Festival programme | Cross-franchise entity links |

### 3.3 Section front matter

Each section carries structured metadata, parsed from a header block, driving stage 3.

```yaml
docType: press-kit      # studio | press-kit | casting | box-office | awards
                        # | series-bible | episode-guide | bio | festival
subject: The Vermilion Hour   # primary film/series/person
year: 2024
studio: Meridian Pictures
market: domestic        # domestic | international | worldwide | null
```

### 3.4 Deliberate traps

The pedagogical payload. Each is authored so a specific question fails at a specific rung and
passes at the next. **Do not treat this table as filler — it is the demo.**

| # | Trap | How to author it | Fixed at |
|---|---|---|---|
| 1 | Split filmography | An actor's credits list broken across a page boundary, so films 1–4 and 5–8 land in different chunks | 2 (overlap), 10 (`:NEXT`) |
| 2 | Superseded casting | A 2023 casting announcement naming one actor, later replaced by a 2024 announcement naming another | 3 (date filter) |
| 3 | Exact box office | "$47.3M domestic opening weekend" appearing once. Embeddings are blind to exact figures | 4 (hybrid) |
| 4 | Buried crew credit | The composer named 400 tokens into a 12-name crew list, ranking ~12th on cosine | 5 (rerank) |
| 5 | Colloquial query | Golden question asks "who did the music for X"; the document says "original score composed by" | 6 (query rewrite) |
| 6 | Orphan pronoun | Episode guide chunk reading "She wins her second Silver Lion for this performance" — no name, no series in the chunk | 7 (contextual) |
| 7 | Two-film compare | "Compare the opening weekends of A and B" — one search cannot serve both | 9 (agentic) |
| 8 | Repeat collaborator | "Which cinematographer has shot more than one film for director D?" — two hops | 10 (traversal) |
| 9 | Studio count | "Which studio released the most films in 2024?" — unanswerable from top-k | 10 (aggregation) |
| 10 | **Connection path** | "How is actor A connected to actor B?" — they never co-starred; the link is 4 hops via a shared director | 10 (`shortestPath`) |
| 11 | **Title collision** | Two films titled *The Thaw* (1998 and 2024, a remake). Ungrounded retrieval blends them | 3 (year filter) + resolution |
| 12 | **Actor/character collision** | An actor named *Marlowe* who plays a character named *Vance*, and a different character named *Marlowe* played by someone else | entity resolution (§6.4) |

Traps 10–12 are new in v3 and are the strongest moments in the demo. Prioritise them.

---

## 4. Stack

| Concern | Choice | Where | Cost |
|---|---|---|---|
| App + API + UI | ASP.NET Core (.NET 9/10) | Local | Free |
| PDF parsing | `UglyToad.PdfPig` | Local, in-process | Free |
| Embeddings | `all-MiniLM-L6-v2` via ONNX Runtime | Local, in-process | Free |
| Reranking | `ms-marco-MiniLM-L-6-v2` via ONNX Runtime | Local, in-process | Free |
| Chat + extraction | Ollama Cloud | Hosted | Free tier |
| Vector store | Qdrant Cloud | Hosted | Free tier |
| Graph store | Neo4j AuraDB | Hosted | Free tier |
| Caches | SQLite | Local file | Free |

**Out of scope:** SQL routing, auth, multi-user, OCR, Docker, cloud deployment.

### 4.1 Rate-limit reality

Extraction is the expensive part. A 35-page PDF is ~220 chunks; extraction plus verification
is ~290 chat calls, enough to strain the Ollama Cloud free tier. Mandatory mitigations:

1. **Extraction cache** keyed by `sha256(chunkText + ontologyVersion + modelId)`. Reprocessing
   an unchanged document makes zero calls.
2. **Quick vs Thorough modes** — Quick skips verification (~1 call/chunk); Thorough adds
   batched verification, 10 triples per call (~1.3 calls/chunk).
3. **Chunk cap** default 120 for extraction, with an explicit UI warning and a
   document-spread sampling option rather than silent truncation.
4. Retry with exponential backoff on 429/503 (3 attempts, 1s/2s/4s), max 2 concurrent calls.

**Ship the demo PDF with pre-warmed caches committed.** Demo day then costs nothing, and you
save live extraction for a short uploaded PDF as a finale.

### 4.2 Service notes

**Ollama Cloud** is not OpenAI-SDK compatible — use `OllamaSharp` or `HttpClient` against
`/api/chat`. Cloud tags carry a `-cloud` suffix and the catalog changes often; validate the
configured tag at startup. Free-tier limits are GPU-time based and unpublished. Make the
extraction model **separately configurable** from the chat model — extraction needs stronger
instruction-following and reliable JSON.

**Qdrant Cloud and Neo4j AuraDB free tiers both pause when idle** (roughly a week); Qdrant
deletes after ~4 weeks. Health must distinguish *paused* from *unreachable* — this is the most
likely demo-day failure.

---

## 5. Processing pipeline

```
POST /api/documents/upload            multipart → documentId
POST /api/documents/{id}/process      → jobId
GET  /api/documents/{id}/status       poll
GET  /api/documents  |  DELETE /api/documents/{id}
```

```
1. Parse      PdfPig → text, page numbers, heading detection
2. Segment    infer sections from font-size heuristics + front-matter blocks
3. Chunk      three strategies in parallel → three Qdrant collections
4. Embed      local ONNX, batched 32, cached
5. Enrich     one LLM call per section → summary, prepended for `contextual` collection
6. Extract    LLM → entities + relations with evidence spans        [§6]
7. Resolve    domain-specific entity canonicalisation               [§6.4]
8. Verify     LLM verification pass (Thorough only)                 [§6.6]
9. Review     PAUSE — human approval in the UI                      [§6.7]
10. Commit    write nodes + edges to Neo4j
11. Derive    compute derived edges (COLLABORATED_WITH)             [§6.9]
```

Stage 9 is a genuine pause awaiting `POST /api/documents/{id}/graph/commit`. It is deliberate
and it is one of the better moments in the demo.

### 5.1 Parsing

Extract per page: text, page number, word bounding boxes. Use font size relative to the
document median for heading detection. Join hyphenated line breaks. Strip lines appearing on
>60% of pages (running headers/footers). Reject scanned PDFs — if median extracted characters
per page is under 50, fail with a clear message.

### 5.2 Chunking → three collections

| Strategy | Collection | Behaviour |
|---|---|---|
| `fixed` | `{docId}_fixed` | 400 tokens, **zero overlap**. Deliberately bad — the stage-1 baseline that breaks trap 1 |
| `recursive` | `{docId}_recursive` | 400 tokens, 80 overlap; split `\n\n` → `\n` → `. ` → hard cut |
| `contextual` | `{docId}_contextual` | Recursive chunks, each prefixed with `"{subject} ({year}, {docType}) — {sectionSummary}"` |

The contextual prefix is domain-tuned: naming the film or series and its year in the prefix is
exactly what fixes trap 6, where an episode-guide chunk has no idea which series it belongs to.

### 5.3 Qdrant payload

```
Vector: 384 dims, Cosine
Payload:
  chunkId    keyword    (indexed)   "{docId}#{seq}"
  docId      keyword    (indexed)
  section    keyword    (indexed)
  docType    keyword    (indexed)   press-kit | casting | box-office | awards | ...
  subject    keyword    (indexed)   film/series/person the section is about
  year       integer    (indexed)   ← powers traps 2 and 11
  studio     keyword    (indexed)
  market     keyword    (indexed)
  page       integer    (indexed)
  seq        integer
  text       text       (FULL-TEXT INDEXED — required for stage 4)
  entityKeys keyword[]  (indexed)   populated after §6.4
```

Full-text indexing on `text` must be enabled at collection creation or stage 4 cannot work.

---

## 6. LLM knowledge-graph extraction

Design principle: **an LLM proposes, deterministic code disposes.**

### 6.1 Not extracted by LLM

Written by code — exact, free, and an LLM could only degrade them:

```
(:Chunk)-[:PART_OF]->(:Document)
(:Chunk)-[:NEXT]->(:Chunk)
(:Chunk)-[:IN_SECTION]->(:Section)
```

### 6.2 Domain ontology — node types

Editable in the UI before processing, stored per document. Constrained, not open-ended:
open extraction is the primary source of graph noise.

| Type | Key format | Notes |
|---|---|---|
| `Film` | `film:{slug}:{year}` | **Year is part of the key** — remakes share titles (trap 11) |
| `TVSeries` | `series:{slug}` | |
| `Season` | `season:{seriesSlug}:{n}` | |
| `Episode` | `episode:{seriesSlug}:s{n}e{m}` | |
| `Person` | `person:{slug}` | Real humans (fictional ones, in this corpus) |
| `Character` | `character:{slug}:{filmOrSeriesSlug}` | **Scoped to its work** — the same character name can recur across unrelated titles |
| `Studio` | `studio:{slug}` | Production company / distributor |
| `Franchise` | `franchise:{slug}` | |
| `Genre` | `genre:{slug}` | Closed vocabulary |
| `Award` | `award:{slug}` | The ceremony, e.g. Silver Lion |
| `AwardCategory` | `awardcat:{awardSlug}:{slug}` | |
| `Festival` | `festival:{slug}` | |
| `Location` | `location:{slug}` | Filming locations |

**Never merge `Person` and `Character`, ever, even on identical names.** This is trap 12 and it
must be enforced in code, not left to the resolver's similarity thresholds.

### 6.3 Domain ontology — relation types

| Predicate | From → To | Edge properties |
|---|---|---|
| `ACTED_IN` | Person → Film \| TVSeries \| Episode | `billing`, `creditedAs` |
| `PLAYED` | Person → Character | |
| `APPEARS_IN` | Character → Film \| TVSeries | |
| `DIRECTED` | Person → Film \| TVSeries \| Episode | |
| `WROTE` | Person → Film \| TVSeries \| Episode | `role` (screenplay/story) |
| `PRODUCED` | Person → Film \| TVSeries | `role` (exec/line/associate) |
| `COMPOSED_FOR` | Person → Film \| TVSeries | |
| `SHOT_BY` | Film \| TVSeries → Person | Cinematographer |
| `EDITED_BY` | Film \| TVSeries → Person | |
| `PRODUCED_BY` | Film \| TVSeries → Studio | |
| `DISTRIBUTED_BY` | Film → Studio | `market` |
| `PART_OF_FRANCHISE` | Film \| TVSeries → Franchise | `order` |
| `SEQUEL_TO` | Film → Film | Ordered chain — traversable like `:NEXT` |
| `PREQUEL_TO` | Film → Film | |
| `REMAKE_OF` | Film → Film | Powers trap 11 |
| `SPINOFF_OF` | TVSeries → Film \| TVSeries | |
| `ADAPTED_FROM` | Film \| TVSeries → Work | |
| `HAS_SEASON` | TVSeries → Season | |
| `HAS_EPISODE` | Season → Episode | `number` |
| `HAS_GENRE` | Film \| TVSeries → Genre | |
| `NOMINATED_FOR` | Person \| Film → AwardCategory | `year` |
| `WON` | Person \| Film → AwardCategory | `year` |
| `PREMIERED_AT` | Film → Festival | `year` |
| `FILMED_AT` | Film \| TVSeries → Location | |
| `ACQUIRED` | Studio → Studio | `year` |
| `RELATED_TO` | any → any | **Fallback — deliberately last resort** |

Track the `RELATED_TO` share. Above ~20% in this domain means the ontology isn't being applied
and the UI should say so. A well-specified film ontology should rarely need the fallback.

### 6.4 Domain-specific entity resolution

The richest part of this domain and worth a slide of its own. Generic cosine-similarity
merging **will produce a wrong graph here.** Apply these rules in order, before any similarity
check:

**Rule 1 — Type barriers are absolute.** Never merge across `Person` / `Character` /
`Film` / `Studio`, regardless of name similarity or embedding distance. An actor named
*Marlowe* and a character named *Marlowe* are two nodes. Full stop.

**Rule 2 — Films require year agreement.** Two `Film` entities with the same normalised title
merge **only** if their years match or one year is unknown. *The Thaw (1998)* and
*The Thaw (2024)* stay separate and get linked by `REMAKE_OF`.

**Rule 3 — Title normalisation.** Strip leading articles (`The`, `A`, `An`) and trailing
article suffixes (`Thaw, The`). Strip subtitle punctuation variants (`:` vs `—` vs `-`).
Normalise roman numerals to digits (`Part II` → `Part 2`). Case-fold, strip diacritics.

**Rule 4 — Person name handling.**
- Normalise: case-fold, strip diacritics and punctuation, drop suffixes (`Jr`, `Sr`, `III`).
- Expand common diminutives via a small lookup table (`Bob`↔`Robert`, `Bill`↔`William`,
  `Kate`↔`Katherine`). Ship the table; it's ~40 pairs and prevents most fragmentation.
- Handle initials: `J. R. Vance` and `James Robert Vance` merge if initials are consistent.
- **Do not merge two `Person` entities with the same name if they hold incompatible role
  clusters in disjoint years.** Flag them for human review instead — this is exactly the
  ambiguity the review gate exists for.

**Rule 5 — Characters are scoped.** Character identity is `(name, work)`. The same character
name in two unrelated films is two nodes. Within a franchise, merge across films only when an
explicit `PART_OF_FRANCHISE` link exists between the works.

**Rule 6 — Similarity, only after 1–5.** Same type, embedding cosine ≥ **0.92**, Jaro-Winkler
≥ **0.88** (raised from 0.85 for this domain — person names are short and false merges are
costly). Canonical name = most frequent surface form; all variants kept in `aliases[]`.

**Rule 7 — Studio suffixes.** Strip `Pictures`, `Studios`, `Entertainment`, `Films`,
`Productions`, `Inc`, `Ltd` before comparison, but retain the full form as the display name.

Report a merge ratio. Healthy range for this domain is **1.3 – 2.8** surface forms per
canonical entity — higher than generic text, because credit lists and prose refer to the same
people in many ways.

### 6.5 Extraction call

One call per chunk. Temperature 0. JSON-only, schema-validated, one reparse retry, then skip
the chunk with a warning.

**Context supplied per call:** the chunk, the section `subject` and `year`, the `docType`, the
section summary, and the **tail of the previous chunk** (last 200 chars). The previous-chunk
tail is what resolves pronouns across boundaries and directly mitigates trap 6.

**Output shape:**

```json
{
  "entities": [
    { "name": "Ilse Vantor", "type": "Person",
      "evidence": "Ilse Vantor returns as Commander Reyes" },
    { "name": "Commander Reyes", "type": "Character",
      "evidence": "Ilse Vantor returns as Commander Reyes" }
  ],
  "relations": [
    { "subject": "Ilse Vantor", "predicate": "PLAYED", "object": "Commander Reyes",
      "evidence": "Ilse Vantor returns as Commander Reyes",
      "confidence": 0.95, "properties": {} },
    { "subject": "Ilse Vantor", "predicate": "ACTED_IN", "object": "The Vermilion Hour",
      "evidence": "Ilse Vantor returns as Commander Reyes",
      "confidence": 0.8, "properties": { "billing": 1 } }
  ]
}
```

**Prompt requirements, domain-tuned:**

- Enumerate permitted node and relation types inline. Forbid inventing new ones.
- `evidence` **must be a verbatim substring of the chunk.** State it, then repeat it. This is
  the highest-leverage instruction in the entire prompt.
- **Require both entities when a performance is described.** "X plays Y" must yield a `Person`,
  a `Character`, and a `PLAYED` relation — never collapse the two into one node.
- **Crew credits carry direction.** Specify that `SHOT_BY` and `EDITED_BY` run Film → Person
  while `DIRECTED` and `WROTE` run Person → Film. Direction errors are the most common
  extraction fault in this domain; a table in the prompt fixes most of them.
- **Never infer credits.** If a person is named in a press kit without a stated role, extract
  the `Person` node and no relation.
- Few-shot examples: one cast-list example, one crew-list example, one prose example, and
  **one negative example** where a plausible but unstated collaboration is correctly omitted.
- `subject` and `object` must both appear in the same response's `entities` array.

### 6.6 Post-extraction filters

Applied in order, deterministically. Each reports its drop count; the UI renders a funnel.

1. **Evidence grounding (hard).** Normalise whitespace and case; require `evidence` to be a
   literal substring of the chunk. Drop failures. This single check removes most hallucinated
   triples, since a fabricated relation rarely arrives with a real supporting span.
2. **Ontology conformance (hard).** Drop non-ontology types and predicates. Do not coerce.
3. **Direction and type check (hard, domain-specific).** Validate each predicate's endpoint
   types against §6.3. A `DIRECTED` edge from a `Film` to a `Person` is inverted — **auto-flip
   it and count the correction** rather than dropping. Report the flip rate; above 15% means
   the prompt's direction table needs work.
4. **Dangling reference (hard).** Drop relations whose endpoints aren't in the same response.
5. **Confidence floor (soft).** Default 0.6. Retain below-floor triples in the review UI,
   marked, rather than deleting them.
6. **Entity resolution.** Per §6.4.
7. **Triple deduplication.** Identical `(subject, predicate, object)` across chunks becomes one
   edge with `mentionCount`, supporting `chunkIds[]`, and confidence = max observed. Repeated
   assertion is a genuine reliability signal — surface `mentionCount` in the UI.

**Verification pass (Thorough mode).** A second LLM call re-judges surviving triples against
their source chunk, batched 10 per call, framed as judge rather than extractor. Verdicts:
`SUPPORTED` passes; `PARTIAL` multiplies confidence by 0.7 and flags; `UNSUPPORTED` drops.
Verdict and reason stored as edge properties so they stay inspectable in Neo4j.

### 6.7 Human review gate

Processing pauses. Triples presented grouped by confidence, with the extraction funnel above:

```
┌────────────────────────────────────────────────────────────────┐
│ Proposed:  118 entities · 194 relations                        │
│ 341 extracted → 262 grounded → 244 conformant (11 flipped)     │
│              → 231 resolved → 194 verified                     │
├────────────────────────────────────────────────────────────────┤
│ ☑ Ilse Vantor ──PLAYED──> Commander Reyes           0.95  ×4   │
│   "Ilse Vantor returns as Commander Reyes"                     │
│   SUPPORTED · p.11 · chunk #27                    [✓] [✗] [↗]  │
├────────────────────────────────────────────────────────────────┤
│ ⚠ Ilse Vantor (Person) — possible duplicate of "I. Vantor"     │
│   disjoint years, incompatible roles          [merge] [keep]   │
├────────────────────────────────────────────────────────────────┤
│ ☐ Meridian Pictures ──ACQUIRED──> Halcyon Films      0.58  ×1  │
│   "…following the Halcyon transaction, Meridian…"              │
│   PARTIAL · acquisition direction unclear         [✓] [✗] [↗]  │
└────────────────────────────────────────────────────────────────┘
```

Bulk actions: accept all, accept above threshold, reject all low-confidence. Ambiguous person
merges from Rule 4 surface here as explicit merge/keep decisions. `[↗]` opens the source chunk
with the evidence span highlighted. A "skip review" button exists for repeat demos, but the
gate is the default. Rejections persist by triple hash so reprocessing doesn't resurface them.

### 6.8 Extraction quality metrics

`GET /api/documents/{id}/extraction/metrics`

| Metric | Healthy range (this domain) |
|---|---|
| Grounding pass rate | > 0.70 |
| Conformance rate | > 0.90 |
| Direction flip rate | < 0.15 |
| Verification pass rate | > 0.75 |
| Entity merge ratio | 1.3 – 2.8 |
| `RELATED_TO` share | < 0.20 |
| Orphan entity rate | < 0.25 |
| Person/Character collision blocks | reported, not bounded — expect several |
| Triples per chunk | 1.5 – 4.0 (higher than generic prose; credit lists are dense) |
| Human rejection rate | < 0.15 |

**Put the funnel on a slide.** "341 proposed, 194 committed" is the most honest thing you can
show about LLM extraction, and it inoculates the room against assuming a model just produces a
correct graph.

### 6.9 Derived edges — computed, not extracted

After commit, compute these in Cypher. They are exact, free, and they power the best queries:

```cypher
// Two people who worked on the same title
MATCH (a:Person)-[:ACTED_IN|DIRECTED|WROTE|PRODUCED|COMPOSED_FOR]->(w)
MATCH (b:Person)-[:ACTED_IN|DIRECTED|WROTE|PRODUCED|COMPOSED_FOR]->(w)
WHERE a.key < b.key
WITH a, b, count(DISTINCT w) AS shared, collect(DISTINCT w.title)[..5] AS titles
MERGE (a)-[c:COLLABORATED_WITH]->(b)
  SET c.count = shared, c.titles = titles, c.derived = true
```

Mark every derived edge `derived: true` and render it differently in the UI. The distinction
between *asserted by a document* and *computed from the graph* is a real lesson, and it's one
most GraphRAG demos blur.

### 6.10 Final schema

```
(:Document {id, title, pageCount, uploadedUtc})
(:Section  {id, docId, docType, subject, year, studio, market, page})
(:Chunk    {id, docId, text, seq, page, section})

(:Film {key, title, year, runtime, releaseDate, boxOfficeDomestic, boxOfficeWorldwide})
(:TVSeries {key, title, startYear, endYear, network})
(:Season {key, number}) (:Episode {key, number, title, airDate})
(:Person {key, name, aliases[], primaryRoles[], mentionCount})
(:Character {key, name, workKey})
(:Studio {key, name, aliases[]}) (:Franchise {key, name})
(:Genre {key, name}) (:Award {key, name}) (:AwardCategory {key, name})
(:Festival {key, name}) (:Location {key, name})

(:Chunk)-[:PART_OF]->(:Document)        deterministic
(:Chunk)-[:NEXT]->(:Chunk)              deterministic
(:Chunk)-[:IN_SECTION]->(:Section)      deterministic
(:Chunk)-[:MENTIONS]->(entity)          LLM, evidence-grounded
(entity)-[r:REL]->(entity)              LLM + verified
    r.predicate, r.confidence, r.mentionCount, r.chunkIds[],
    r.evidence, r.verdict, r.verdictReason, r.properties, r.flipped
(:Person)-[:COLLABORATED_WITH]->(:Person)   derived
```

`:Chunk` nodes hold **no embedding**. `chunkId` matches the Qdrant payload. That shared
identifier is the entire vector↔graph integration.

```cypher
CREATE CONSTRAINT chunk_id  IF NOT EXISTS FOR (c:Chunk)  REQUIRE c.id  IS UNIQUE;
CREATE CONSTRAINT film_key  IF NOT EXISTS FOR (f:Film)   REQUIRE f.key IS UNIQUE;
CREATE CONSTRAINT person_key IF NOT EXISTS FOR (p:Person) REQUIRE p.key IS UNIQUE;
CREATE CONSTRAINT char_key  IF NOT EXISTS FOR (c:Character) REQUIRE c.key IS UNIQUE;
CREATE INDEX film_title     IF NOT EXISTS FOR (f:Film)   ON (f.title);
CREATE INDEX person_name    IF NOT EXISTS FOR (p:Person) ON (p.name);
```

Commit in `UNWIND` batches of 500 using `MERGE` so re-commit is idempotent.

---

## 7. The ladder — query endpoints

```
POST /api/ask/stage/{n}      n = 0..11   (preset — one click per demo point)
POST /api/ask                            (explicit flags — UI toggle panel)
```

### 7.1 Request

```json
{
  "documentId": "doc_a1b2",
  "question": "Who composed the score for The Vermilion Hour?",
  "goldenId": "q017",
  "options": {
    "collection": "recursive",
    "topK": 5, "candidateK": 50,
    "useMetadataFilter": false,
    "filter": { "docType": null, "year": null, "yearRange": null,
                "studio": null, "subject": null },
    "useHybrid": false, "useRerank": false, "useQueryRewrite": false,
    "useGraphExpansion": false,
    "graphMode": "expand",
    "graphHops": { "next": true, "parent": true, "entity": false, "entityRel": false },
    "maxPathHops": 6,
    "minEdgeConfidence": 0.6,
    "includeDerivedEdges": true,
    "useAgentic": false, "useRouter": false, "skipRetrieval": false
  }
}
```

`graphMode` is one of `expand` | `path` | `aggregate` — new in v3, see §7.3.

### 7.2 Stage presets (cumulative)

| n | Name | Options | Teaches | Trap fixed |
|---|---|---|---|---|
| 0 | No RAG | `skipRetrieval` | Hallucination baseline | — |
| 1 | Naive RAG | `collection: fixed` | The core loop | — |
| 2 | Chunking | `collection: recursive` | Overlap and boundaries | 1 |
| 3 | Metadata filter | `useMetadataFilter` | Right title, right year | 2, 11 |
| 4 | Hybrid search | `useHybrid` | Embeddings can't do exact figures | 3 |
| 5 | Reranking | `candidateK: 50, useRerank` | Retrieve wide, rank precise | 4 |
| 6 | Query rewrite | `useQueryRewrite` | Users don't write like press kits | 5 |
| 7 | Contextual chunks | `collection: contextual` | Orphan chunks lack referents | 6 |
| 8 | Citations | + groundedness | Trust and verification | — |
| 9 | Agentic | `useAgentic` | Multi-part needs multi-search | 7 |
| 10 | Graph | `useGraphExpansion`, all modes | Relations, paths, counts | 8, 9, 10 |
| 11 | Router | `useRouter` | Not every query needs every layer | — |

### 7.3 Stage 10 — three modes

**Mode `expand`** — vector/hybrid yields seeds, Cypher expands:

```cypher
MATCH (c:Chunk) WHERE c.id IN $ids
OPTIONAL MATCH (prev)-[:NEXT]->(c)
OPTIONAL MATCH (c)-[:NEXT]->(next)
OPTIONAL MATCH (c)-[:MENTIONS]->(e)
OPTIONAL MATCH (e)-[r:REL]->(e2) WHERE r.confidence >= $minConf
OPTIONAL MATCH (c2:Chunk)-[:MENTIONS]->(e2)
RETURN c.id AS id, c.text AS text,
       prev.text AS prevText, next.text AS nextText,
       collect(DISTINCT {name: e.name, type: labels(e)[0]}) AS entities,
       collect(DISTINCT {pred: r.predicate, target: e2.name,
                         conf: r.confidence, viaChunk: c2.id}) AS related
```

**Mode `path`** — the six-degrees query. **This is the demo's best moment; build it properly.**

```cypher
MATCH (a:Person {key: $from}), (b:Person {key: $to})
MATCH path = shortestPath(
  (a)-[:ACTED_IN|DIRECTED|WROTE|PRODUCED|COMPOSED_FOR|SHOT_BY|EDITED_BY*..12]-(b)
)
RETURN [n IN nodes(path) | {name: coalesce(n.name, n.title),
                            type: labels(n)[0], key: n.key}] AS nodes,
       [r IN relationships(path) | type(r)] AS rels,
       length(path) AS hops
```

The narrative is then rendered from the path — "A acted in *The Thaw*, which was directed by
D, who also directed *Vermilion*, in which B acted" — and the answer is *constructed from the
traversal*, not generated from retrieved text. Say that out loud during the demo. The LLM's
only job here is to phrase a path the graph already computed.

The UI must render the path as a horizontal node-edge chain, animated hop by hop.

**Mode `aggregate`** — pure Cypher, no vector search at all. Ship these as one-click presets:

```cypher
-- Which studio released the most films in a given year
MATCH (f:Film)-[:PRODUCED_BY]->(s:Studio)
WHERE f.year = $year
RETURN s.name AS studio, count(f) AS films ORDER BY films DESC

-- Most frequent director/cinematographer pairing
MATCH (d:Person)-[:DIRECTED]->(f:Film)-[:SHOT_BY]->(c:Person)
RETURN d.name AS director, c.name AS cinematographer,
       count(f) AS films, collect(f.title) AS titles
ORDER BY films DESC

-- Actors appearing in more than one franchise
MATCH (p:Person)-[:ACTED_IN]->(f:Film)-[:PART_OF_FRANCHISE]->(fr:Franchise)
WITH p, count(DISTINCT fr) AS franchises, collect(DISTINCT fr.name) AS names
WHERE franchises > 1 RETURN p.name, franchises, names ORDER BY franchises DESC

-- Award tally by studio
MATCH (f:Film)-[:PRODUCED_BY]->(s:Studio)
MATCH (f)-[w:REL {predicate:'WON'}]->(:AwardCategory)
RETURN s.name AS studio, count(w) AS wins ORDER BY wins DESC
```

The `minEdgeConfidence` slider re-runs these live. Watching a studio's award tally change as
you drag the threshold is the most honest illustration of extraction uncertainty you can give.

### 7.4 Strict flow isolation — mandatory

- **No answer caching across stages.** Cache key includes `documentId`, question, and every
  resolved flag. Two stages must never share a cached answer.
- **No parametric fallback.** For stages 1–11: answer only from provided context; if
  insufficient, reply exactly `Not found in the provided documents.` **This matters more in
  this domain than any other** — a model asked about films will happily answer from its
  training data, and an ungrounded correct-sounding answer would silently destroy the demo.
  Add a golden question about a *real* film absent from the fictional corpus and verify every
  stage refuses it. Put that on a slide.
- **Stage 0 is the sole exception**, visually flagged as unconstrained. Ask it the same
  real-film question and let it answer confidently — the contrast is the whole point of rung 0.
- No conversation history. No cross-stage retrieval reuse.

### 7.5 Stage notes

**Stage 3.** Domain filters: `year`, `yearRange`, `docType`, `studio`, `subject`. Trap 11 is
fixed by year filtering; trap 2 by `docType: casting` plus a `minYear`.

**Stage 4.** Qdrant native hybrid, RRF `k = 60`. Label candidates `vector` / `keyword` /
`both`. In this domain the keyword arm carries proper nouns and figures — showing that
"$47.3M" was found only by keyword is the clearest possible argument for hybrid.

**Stage 5.** Retrieve 50, rerank locally, keep 5. Return `rankBefore`, `rankAfter`,
`droppedCount`. The buried composer credit moving 12 → 1 is the most persuasive moment.

**Stage 6.** One call, JSON only: `{"rewritten": "...", "keywords": [...]}`. Domain-tune the
prompt with a glossary: *music* → *score / composer*; *filmed by / shot by* → *cinematographer
/ director of photography*; *the guy who made* → *director*; *made money* → *box office gross*.

**Stage 9.** Bounded loop: max 4 iterations, max 6 chat calls, hard stop. One tool,
`search(query, filter)`. Every iteration appends to `trace[]`. On cap, return a partial answer
plus a warning — never loop.

**Stage 11.** Classification call → `lookup | relational | path | aggregation | multi_part` →
dispatch. `trace[]` records the classification and route. **Include a deliberate misroute in
the golden set** — showing the router getting it wrong is more honest and more memorable than
showing it always right.

### 7.6 Response envelope

As v2 §6.6, with the `graph` block extended:

```json
"graph": {
  "mode": "path",
  "seedChunkIds": [],
  "entitiesTouched": [...],
  "edgesTraversed": [
    { "from": "Ilse Vantor", "predicate": "ACTED_IN", "to": "The Thaw (2024)",
      "confidence": 0.95, "mentionCount": 4, "derived": false }
  ],
  "path": {
    "hops": 4,
    "nodes": [
      { "name": "Ilse Vantor", "type": "Person" },
      { "name": "The Thaw", "type": "Film", "year": 2024 },
      { "name": "Dara Okonjo", "type": "Person" },
      { "name": "Vermilion Hour", "type": "Film", "year": 2024 },
      { "name": "Piet Hansen", "type": "Person" }
    ],
    "rels": ["ACTED_IN", "DIRECTED", "DIRECTED", "ACTED_IN"]
  },
  "aggregationResult": null
}
```

---

## 8. Golden set

`corpus/demo/golden.json` — 44 questions, 4 per type.

Types: `simple_lookup`, `exact_figure`, `boundary`, `superseded`, `terse`, `orphan_context`,
`title_collision`, `multi_part`, `multi_hop`, `path`, `aggregation`.

Plus a mandatory `ungrounded` control group of 4 questions about **real films absent from the
corpus**, whose `expectedAnswer` is the refusal string. Every stage 1–11 must refuse; stage 0
must not. This group is the honesty check for the entire demo.

```json
{
  "id": "q031",
  "question": "How is Ilse Vantor connected to Piet Hansen?",
  "type": "path",
  "expectedChunkIds": [],
  "expectedPathContains": ["Dara Okonjo"],
  "notes": "They never co-starred. 4 hops via a shared director. Unanswerable below stage 10."
}
```

Auto-generation for uploaded PDFs remains available (`POST /api/documents/{id}/golden/generate`)
but must be labelled as weaker evidence — a question generated *from* a chunk is biased toward
being retrievable *by* that chunk. Use the hand-authored demo set for the actual presentation.

**Eval** breaks down by question type, not just overall. The overall curve is smooth and
teaches nothing; the per-type heatmap shows stage 4 fixing `exact_figure` while doing nothing
for `path`. A `regressions[]` array is required — find at least one rung that makes something
worse and slide it.

---

## 9. UI

Single page from `wwwroot`. Vanilla JS + `fetch`. No framework, no build step. F5 and nothing else.

```
[Documents] [Process] [Review] [Ask] [Compare] [Eval] [Graph] [Explore]
```

Tabs as v2 §9, with these domain changes:

**Review tab** additionally surfaces person-merge ambiguities (Rule 4) as explicit merge/keep
decisions, and shows Person/Character collision blocks as a counter — proof the type barrier
is doing work.

**Graph tab** — node colour by type with a fixed legend (Person, Character, Film, TVSeries,
Studio, Award). Derived `COLLABORATED_WITH` edges rendered dashed. Confidence slider prunes
live. Click an edge for its evidence span and source chunk.

**Explore tab (new)** — the crowd-pleaser. Two person pickers and a "Connect" button running
the `path` query, rendering the chain hop by hop with the connecting titles named. Add a
"random pair" button. This tab will get more engagement than everything else combined, so make
it the finale rather than burying it.

**Presentation mode** `?present=1` — larger type, toggles hidden, stage name / question /
answer / top-5 chunks only. Projectors are unforgiving.

---

## 10. Configuration

As v2 §10, plus:

```json
"Domain": {
  "OntologyPath": "config/film-ontology.json",
  "DiminutivesPath": "config/name-diminutives.json",
  "StudioSuffixes": ["Pictures","Studios","Entertainment","Films","Productions","Inc","Ltd"],
  "TitleArticles": ["The","A","An"],
  "EntityMergeCosine": 0.92,
  "EntityMergeJaroWinkler": 0.88,
  "MaxPathHops": 12,
  "BlockCrossTypeMerge": true
}
```

---

## 11. Build phases

Each must be demonstrable before the next begins.

1. **Foundation** — ONNX models, caches, Qdrant, Neo4j, Ollama with retry.
   *Accept:* health green; similar sentences cosine > 0.7, unrelated < 0.3.
2. **Corpus + parse** — author the Meridian universe PDF with all 12 traps; PdfPig extraction
   with pages, headings, front matter.
   *Accept:* front matter parses to correct `year`/`docType`/`subject` per section.
3. **Chunk, embed, index** — three strategies, three collections, full-text index.
   *Accept:* warm-cache reprocess under 5s with zero embedder calls.
4. **Stages 0–2 + Ask/Compare** — response envelope populated.
   *Accept:* trap 1 fails at stage 1, passes at stage 2, visible side by side.
5. **Stages 3–7** — filter, hybrid, rerank, rewrite, contextual.
   *Accept:* traps 2, 3, 4, 5, 6, 11 each flip fail→pass at exactly their designated stage
   and no earlier.
6. **Extraction** — LLM extraction, seven filters, verification, review UI, commit, derived edges.
   *Accept:* grounding > 0.70, flip rate < 0.15, `RELATED_TO` < 0.20, zero Person/Character
   merges. Hand-check 20 committed triples — at least 18 defensible against their evidence.
7. **Stage 10 all modes + Graph + Explore** — expand, path, aggregate.
   *Accept:* trap 10 answered only at stage 10 in `path` mode; the path renders visually;
   moving the confidence slider visibly changes an aggregation answer.
8. **Stages 8, 9, 11 + Eval** — citations, agentic, router, heatmap.
   *Accept:* the `ungrounded` control group is refused by every stage 1–11 and answered by
   stage 0; full eval run completes without a rate limit.
9. **Polish** — presentation mode, README, model fetch script, committed pre-warmed caches,
   replay recordings.
   *Accept:* a colleague clones, adds three keys, and reaches a working demo from the README.

---

## 12. Failure handling

- Every hosted call try/catch → `warnings[]` + partial response. A rate-limited chat call must
  still return retrieval results; the retrieval half is the interesting half.
- Backoff on 429/503: 3 attempts, 1s/2s/4s.
- Extraction failures are per-chunk and non-fatal — skip, warn, continue. Malformed JSON gets
  one reparse retry, then the chunk is skipped and counted.
- Paused Qdrant/Neo4j → specific actionable message, never a stack trace.
- **Replay mode** `--replay` serves recorded responses from `./recordings/*.json`. Record a
  full pass over the golden set at every stage before the session. Worth the hour it costs.

---

## 13. Non-goals

- No auth, multi-tenancy, or user accounts.
- No SQL/relational routing rung.
- No OCR — text-layer PDFs only.
- No ingestion of scripts, screenplays, subtitles, or published reviews.
- No synthetic prose about real named people.
- No production deployment, Docker packaging, or CI.
- No streaming — complete traces matter more than perceived speed.
- No conversation history; every question is independent.
- Single user, one document at a time, ~600 chunks.
