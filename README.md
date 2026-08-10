# RAG Ladder — film & television

Upload a film-industry PDF, process it into three vector collections and an LLM-extracted
knowledge graph, then ask the same question at each of twelve pipeline stages and watch the answer
change. Built from [RAG-LADDER-DEMO-SPEC-v3-film.md](RAG-LADDER-DEMO-SPEC-v3-film.md).

The audience is engineers unfamiliar with RAG internals, and the format is a live demo:
**everything asserted on a slide is reachable by clicking something in the UI** — retrieved chunks,
per-arm scores, rank deltas, extracted triples, evidence spans, traversal paths, timings, and the
exact prompt that was sent.

---

## Quick start

```powershell
dotnet run --project tools/RagLadder.CorpusBuilder   # render the demo PDF
dotnet run --project src/RagLadder.Api               # http://localhost:5099
```

Then in the browser: **Documents → Load committed demo corpus → Process → Review → Commit → Ask**.

That works with zero configuration and zero credentials, because the app ships with local
fallbacks for every hosted service. To get the real thing, add three keys and two model files.

> **[OPERATIONS.md](OPERATIONS.md) is the full runbook** — prerequisites, account setup, key
> configuration, verification, demo-day preparation and troubleshooting. Start there if you are
> setting this up for the first time or preparing to present it.

### The three keys

Put them in `src/RagLadder.Api/appsettings.Development.json` (gitignored by convention) or in
environment variables:

```json
{
  "RagLadder": {
    "Providers": { "Vector": "qdrant", "Graph": "neo4j", "Chat": "ollama" },
    "Ollama": { "ApiKey": "..." },
    "Qdrant": { "Url": "https://....qdrant.io:6333", "ApiKey": "..." },
    "Neo4j":  { "Uri": "neo4j+s://....databases.neo4j.io", "Password": "..." }
  }
}
```

```powershell
$env:RagLadder__Ollama__ApiKey = '...'
$env:RagLadder__Qdrant__Url    = '...'
$env:RagLadder__Qdrant__ApiKey = '...'
$env:RagLadder__Neo4j__Uri      = '...'
$env:RagLadder__Neo4j__Password = '...'
```

### The two model files

```powershell
pwsh tools/fetch-models.ps1
```

Downloads `all-MiniLM-L6-v2` (embeddings) and `ms-marco-MiniLM-L-6-v2` (reranking) into `models/`,
about 180 MB in total. Both run in-process through ONNX Runtime and cost nothing.

Check `GET /api/health` afterwards: the embedder probe should report the similar pair above 0.7
cosine and the unrelated pair below 0.3. That is the phase 1 acceptance test from the spec.

---

## What runs where

| Concern | Hosted (spec §4) | Alternatives |
|---|---|---|
| App, API, UI | ASP.NET Core (.NET 9) | — |
| PDF parsing | PdfPig, in-process | — |
| Embeddings | `all-MiniLM-L6-v2`, ONNX in-process | Ollama-served, or a deterministic hashing stand-in |
| Reranking | `ms-marco-MiniLM-L-6-v2`, ONNX in-process | chat-model scoring, or a lexical stand-in |
| Chat + extraction | Ollama Cloud | local Ollama in Docker, any OpenAI-compatible endpoint, or `--replay` |
| Vector store | Qdrant Cloud | SQLite, brute-force cosine + BM25 |
| Graph store | Neo4j AuraDB | SQLite, in-memory BFS traversal |
| Caches | SQLite | — |

Every hosted dependency has a route that works on a locked-down network — including one where
both `huggingface.co` and `ollama.com` are blocked. See [OPERATIONS.md §5–6](OPERATIONS.md).

**The fallbacks are a deliberate addition to the spec, not a substitute for it.** They exist so a
colleague can clone the repo and reach a working demo before hunting for credentials, and so the
test suite can exercise the whole pipeline offline. They are never silent: `/api/health` reports
each one as `degraded` with a sentence explaining what is worse about it, and the UI shows that
status in the header. Do not demo on the fallback embedder — it is a bag of words, and the
retrieval quality is not representative.

Provider selection lives in `RagLadder:Providers`. With `FallbackToLocal: true` (the default), a
hosted provider that has no credentials configured falls back rather than failing startup.

---

## The ladder

`POST /api/ask/stage/{n}` for the preset, `POST /api/ask` for explicit flags. Presets are
cumulative — stage *n* keeps everything stage *n−1* turned on.

| n | Name | Teaches | Trap fixed |
|---|---|---|---|
| 0 | No RAG | Hallucination baseline | — |
| 1 | Naive RAG | The core loop | — |
| 2 | Chunking | Overlap and boundaries | 1 |
| 3 | Metadata filter | Right title, right year | 2, 11 |
| 4 | Hybrid search | Embeddings can't do exact figures | 3 |
| 5 | Reranking | Retrieve wide, rank precise | 4 |
| 6 | Query rewrite | Users don't write like press kits | 5 |
| 7 | Contextual chunks | Orphan chunks lack referents | 6 |
| 8 | Citations | Trust and verification | — |
| 9 | Agentic | Multi-part needs multi-search | 7 |
| 10 | Graph | Relations, paths, counts | 8, 9, 10 |
| 11 | Router | Not every query needs every layer | — |

Stage 10 has three modes. `expand` seeds from vector search and walks the graph; `path` runs
`shortestPath` between two people and constructs the answer from the traversal rather than
generating it from retrieved text; `aggregate` skips vector search entirely and counts in Cypher.

### Flow isolation

Non-negotiable, and enforced in code (spec §7.4):

- The answer cache key covers the document, the question, **and every resolved flag**, so two rungs
  can never share a completion. `StagePresetTests.Every_stage_has_a_distinct_cache_key` pins this.
  Answers are cached whole and persisted to SQLite, bounded to the fifty most recently used, so a
  warmed demo survives a restart; `POST /api/ask/warm` fills it and `AnswerCacheTests` pins the
  eviction. Because the key already covers document, question and flags, a stale answer is not
  possible — only a slow one.
- Stages 1–11 answer only from retrieved context. When it is insufficient they reply with exactly
  `Not found in the provided documents.`
- Stage 0 is the sole exception and is flagged `unconstrained` in the response and the UI.
- No conversation history. Every question is independent.

The golden set is `corpus/demo/golden.json`: **52 hand-authored questions, four per type across
thirteen types**. That is the spec's eleven, plus `name_collision` for trap 12, plus four
**ungrounded controls** about real films absent from the corpus. Every stage 1–11 must refuse the
controls and stage 0 must not. That group is the honesty check for the entire demo — put it on a
slide.

`name_collision` is worth a word. Trap 12 differs between the two source documents: spec §3.4 has
an actor/character name collision, the corpus appendix has *Loki* as both a Character and a
TVSeries. This build follows the corpus. Unlike every other type, it is fixed by **entity
resolution rather than by any rung**, so its heatmap row should stay flat across the ladder — which
is itself the point worth showing. The graph-side guarantee is that the series, the character and
the performer remain three nodes; `A_series_and_a_character_sharing_a_name_never_merge` pins it,
and the review tab reports the collision count as proof the type barrier did work.

---

## The corpus and its traps

The demo corpus is [marvel-corpus-srilanka-full.md](marvel-corpus-srilanka-full.md): real
Marvel/X-Men/Spider-Man/Blade **titles** used as familiar labels, with every person, studio,
location, award and financial figure invented.

**Why it is counterfactual, and why it must stay that way.** The chat model already knows the real
credits. If the corpus matched reality, a correct answer would prove nothing — the model could
bypass retrieval entirely and every rung would look equally good. Because every credit is invented,
*any* correct answer is proof that retrieval worked. The corpus is a canary.

`tools/RagLadder.CorpusBuilder` renders it to `corpus/demo/serendib-dossier.pdf`. Two things it
does are about the demo rather than the rendering:

- **Both appendices are stripped**, from `# APPENDIX A` onward. The corpus only asks for Appendix B
  (the answer key) to be removed, but Appendix A is worse: it is the trap map, and it writes out the
  stage-10 connection path in plain text. Ingesting that hands the retriever the answer to the very
  question the traversal is supposed to earn. Pass `--strip-from "# APPENDIX B"` to keep it.
- **A page break is forced** after the anchors in `corpus/demo/pagebreaks.json`, so trap 1 is
  reproducible instead of depending on where text happens to flow.

Trap 1 also depends on how the baseline chunks. The `fixed` strategy is **structure-blind by
design**: it walks the extracted text page by page and cuts every 400 tokens with no overlap,
exactly as a naive "extract page, chunk page" pipeline does. `recursive` and `contextual` chunk per
section. That difference is the whole of stage 2, and
`Trap_one_splits_the_filmography_under_fixed_chunking_and_not_under_recursive` pins it.

---

## Extraction: an LLM proposes, deterministic code disposes

One call per chunk, temperature 0, JSON only, schema-validated, one reparse retry, then the chunk
is skipped and counted. Every surviving triple then runs the filter chain (spec §6.6), each stage
reporting its drop count so the UI can render the funnel:

1. **Evidence grounding (hard)** — the `evidence` value must be a literal substring of the chunk
   after whitespace and typographic normalisation. This single check removes most hallucinations.
2. **Ontology conformance (hard)** — non-ontology types and predicates are dropped, not coerced.
3. **Direction and type check (hard)** — an inverted edge is a correctable mistake, so it is
   *flipped and counted* rather than dropped. A flip rate above 15% means the prompt needs work.
4. **Dangling reference (hard)** — both endpoints must appear in the same response.
5. **Confidence floor (soft)** — below-floor triples are marked, not deleted.
6. **Entity resolution** — see below.
7. **Deduplication** — identical triples merge, carrying `mentionCount` and every supporting chunk.

Thorough mode adds a verification pass: a second call, batched ten triples at a time, framed as a
judge rather than an extractor. `SUPPORTED` passes, `PARTIAL` multiplies confidence by 0.7 and
flags, `UNSUPPORTED` drops.

Then processing **stops** and waits for `POST /api/documents/{id}/graph/commit`. The review gate is
a real halt, not a progress bar.

### Entity resolution is domain-specific

Generic cosine-similarity merging produces a wrong graph here. The rules run in order, and
similarity is consulted only at the end (spec §6.4):

1. **Type barriers are absolute.** Person / Character / Film / Studio never merge. Enforced by key
   construction, not by a threshold — an actor named Marlowe and a character named Marlowe have
   different key prefixes and cannot collide.
2. **Films require year agreement.** *Fantastic Four* (2005) and (2015) stay separate.
3. Title normalisation: leading and trailing articles, subtitle punctuation, roman numerals.
4. Person names: diminutives (a shipped ~85-pair table), suffixes, initials. Same name with
   incompatible role clusters in disjoint years is **flagged for a human**, not merged.
5. Characters are scoped to their work.
6. Only now: cosine ≥ 0.92 **and** Jaro-Winkler ≥ 0.88.
7. Studio suffixes stripped for comparison, full form kept for display.

### Derived ≠ asserted

`COLLABORATED_WITH` edges are computed in Cypher after commit and marked `derived: true`, rendered
dashed in the Graph tab. The distinction between *asserted by a document* and *computed from the
graph* is a real lesson, and most GraphRAG demos blur it.

---

## API

```
POST   /api/documents/upload                       multipart -> documentId
POST   /api/documents/load-demo                    attach the committed demo PDF
POST   /api/documents/{id}/process                 -> jobId
GET    /api/documents/{id}/status                  poll
GET    /api/documents/{id}/chunks?strategy=&take=
GET    /api/documents  |  DELETE /api/documents/{id}

GET    /api/documents/{id}/extraction              the proposed graph at the review gate
GET    /api/documents/{id}/extraction/metrics      funnel + health bands
POST   /api/documents/{id}/review/decisions        accept / reject / reject-below-confidence
POST   /api/documents/{id}/review/merge            merge | keep for an ambiguous person
POST   /api/documents/{id}/graph/commit            resume at step 10

POST   /api/ask/stage/{n}                          n = 0..11
POST   /api/ask                                    explicit flags
POST   /api/compare                                same question, several rungs
GET    /api/stages
POST   /api/ask/warm                               answer at every rung, fill the cache
GET    /api/ask/cache                              what is warm, newest first
DELETE /api/ask/cache

GET    /api/documents/{id}/graph                   snapshot for the graph tab
GET    /api/documents/{id}/graph/path?from=&to=
GET    /api/documents/{id}/graph/aggregate?preset=
GET    /api/documents/{id}/graph/entities?type=&q=
GET    /api/documents/{id}/graph/edge?from=&predicate=&to=
GET    /api/graph/presets  |  GET /api/ontology

POST   /api/documents/{id}/golden/load             the hand-authored set
POST   /api/documents/{id}/golden/generate         weaker evidence, labelled as such
POST   /api/documents/{id}/eval                    -> runId
GET    /api/eval/{runId}

GET    /api/health  |  GET /api/config
```

---

## Cost and rate limits

Extraction is the expensive part. Mandatory mitigations from spec §4.1 are all in place:

- **Extraction cache** keyed by `sha256(chunkText + ontologyVersion + modelId)`. Reprocessing an
  unchanged document makes zero model calls; embeddings are cached the same way, and
  `Reprocessing_an_unchanged_document_makes_no_embedder_calls` pins it.
- **Quick vs Thorough** — quick skips verification.
- **Chunk cap**, default 120, with an explicit warning and document-spread sampling rather than
  silent truncation. The warning always states how many chunks were left out.
- **Backoff** on 429/503: three attempts at 1s/2s/4s, max two concurrent calls.

Ollama Cloud is not OpenAI-SDK compatible, so the client talks to `/api/chat` directly. Cloud tags
carry a `-cloud` suffix and the catalog changes often, so the configured tags are validated against
`/api/tags` at startup and a missing tag is reported as degraded rather than fatal. The extraction
model is configured separately from the chat model.

Qdrant Cloud and Neo4j AuraDB free tiers **pause when idle**, which is the most likely demo-day
failure. Health distinguishes *paused* from *unreachable* and says what to do about it.

### Replay mode

```powershell
dotnet run --project src/RagLadder.Api -- --record     # capture a full pass
dotnet run --project src/RagLadder.Api -- --replay     # serve it back, no network
```

Record a full pass over the golden set at every stage before the session. An unrecorded prompt in
replay mode returns a flagged failure rather than a silent fabrication.

---

## The Ask tab is a chat

Pick a stage, type, press Enter. Each exchange is a chat turn carrying a stage badge, so asking
the same question at two rungs builds the comparison in one transcript — which is the demo.

Evidence is collapsed rather than removed. Every answer carries a **show the work** disclosure
holding the retrieved chunks with their scores and arms, the graph block, the trace, and the exact
prompt that was sent. The spec's rule still holds — everything asserted is reachable by clicking —
but the default view reads as a conversation, not a dashboard.

## Presentation mode

`http://localhost:5099/?present=1` — larger type, toggles hidden, stage name / question / answer /
top chunks only. Projectors are unforgiving.

The **Explore** tab is the finale: two person pickers and a Connect button running `shortestPath`,
rendering the chain hop by hop with the connecting titles named. Vector search cannot answer that
question at any *k*.

---

## Layout

```
src/RagLadder.Api/           app, API and UI
  Parsing/    Chunking/      PdfPig extraction, three chunk strategies
  Embedding/  Reranking/     ONNX in-process, with dev stand-ins
  Vector/     Graph/         Qdrant + Neo4j, each with a SQLite local provider
  Extraction/                prompts, filter chain, entity resolution, verification
  Ask/                       retrieval, rewrite, graph modes, agentic, router, citations
  Eval/                      golden-set runner, per-type heatmap, regressions
  wwwroot/                   single page, vanilla JS, no build step
tools/RagLadder.CorpusBuilder/   markdown -> demo PDF
tools/fetch-models.ps1           ONNX models, with a mirror fallback
tools/setup-ollama.ps1           local Ollama in Docker, for blocked networks
tools/check-network.ps1          which setup route this network allows
tools/benchmark-models.ps1       per-model answer latency on this machine
tools/smoke-test.ps1             end-to-end check against a running instance
tools/mock-openai.ps1            offline test double, never for demos
docker-compose.yml               the local Ollama service
tests/RagLadder.Tests/       unit tests + an offline end-to-end pipeline test
config/                      film-ontology.json, name-diminutives.json
corpus/demo/                 the PDF, golden.json, pagebreaks.json
```

```powershell
dotnet test
```

86 tests. The integration suite boots the real application against the local providers and a
scripted model — a rule-based reader of the corpus's own credit formatting — then walks the whole
pipeline: load, process, review gate, commit, traverse, aggregate, and ask across the ladder.
No network, no API key.

---

## Non-goals

No auth, multi-tenancy or user accounts. No SQL routing rung. No OCR — text-layer PDFs only, and a
scanned PDF is rejected with a clear message. No Docker, CI or cloud deployment. No streaming;
complete traces matter more than perceived speed. No conversation history. Single user, one
document at a time.

## Legal

Never ingest screenplays, scripts, subtitles or published reviews. Never generate synthetic prose
about real named people. Real data, if used at all, is limited to factual metadata from Wikidata
(CC0) or the IMDb non-commercial datasets, attributed in the UI. The shipped corpus is entirely
fictional apart from the titles, which are used only as familiar labels.
