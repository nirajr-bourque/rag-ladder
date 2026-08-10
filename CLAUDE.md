# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A teaching demo built from [RAG-LADDER-DEMO-SPEC-v3-film.md](RAG-LADDER-DEMO-SPEC-v3-film.md):
upload a film-industry PDF, process it into three vector collections plus an LLM-extracted
knowledge graph, then answer the same question at each of twelve pipeline stages ("rungs") and
watch the answer change.

[README.md](README.md) covers the architecture and the API surface;
[OPERATIONS.md](OPERATIONS.md) is the setup and demo-day runbook. This file covers what neither
does: the decisions that will bite you.

## Commands

```powershell
dotnet run --project tools/RagLadder.CorpusBuilder   # markdown -> corpus/demo/serendib-dossier.pdf
dotnet run --project src/RagLadder.Api               # http://localhost:5099
dotnet test                                          # 86 tests (1 needs Neo4j creds)
dotnet test --filter "FullyQualifiedName~Trap_one"   # a single test
pwsh tools/fetch-models.ps1                          # ~180 MB of ONNX models into models/
```

The app runs with **zero configuration**: local SQLite providers stand in for Qdrant and Neo4j, and
deterministic stand-ins for the ONNX embedder and reranker. Health reports every fallback as
`degraded` with a sentence saying what is worse about it. Never demo on the fallback embedder — it
is a bag of words.

`--replay` serves recorded model responses from `recordings/` and never touches the network;
`--record` captures them.

## Things that will bite you

**Configured paths are resolved against the repo root, not the working directory.**
`Configuration/RepoPaths.cs` walks up looking for `RagLadder.sln`. That is why `models/`,
`corpus/`, `config/` and `data/` in appsettings are written as bare relative paths and still work
from anywhere. The content root is separately pinned to the binary's directory in `Program.cs`.

**Get-only collection properties break the review gate.** `ExtractionResult` is persisted to SQLite
as JSON between extraction and commit. `System.Text.Json` will not populate a get-only collection
on the way back in, so `ChunkIds`, `Aliases` and `Drops` all need `init` setters. Losing `ChunkIds`
silently commits a graph with no `MENTIONS` edges — the stage-10 entity hop then returns nothing
while still looking like it worked.
`Stage_ten_expansion_reaches_entities_through_chunk_provenance` guards this.

**The running-line stripper must only look at page margins.** Frequency alone is not enough: this
corpus repeats `studio: Sinharaja Studios` and `market: null` in the body of nearly every page, and
stripping those destroys the metadata stage 3 depends on. `PdfDocumentParser.RemoveRunningLines`
restricts candidates to `RelativeY` above 0.90 or below 0.10.

**The `fixed` strategy is structure-blind on purpose.** It chunks page by page at 400 tokens with
zero overlap, ignoring sections; `recursive` and `contextual` chunk per section. If `fixed` ever
becomes section-aware, stage 1 and stage 2 answer identically and trap 1 stops teaching anything.
`Trap_one_splits_the_filmography_under_fixed_chunking_and_not_under_recursive` pins it.

**Both appendices are stripped from the PDF, not just Appendix B.** The corpus only asks for
Appendix B (the answer key) to be removed, but Appendix A is the trap map and writes out the
stage-10 connection path in plain text — ingesting it hands the retriever the answer the traversal
is supposed to earn. Default `--strip-from` is `# APPENDIX A`.

**Trap 1 depends on a forced page break.** `corpus/demo/pagebreaks.json` lists text anchors after
which the builder inserts a page break, so the split filmography is reproducible rather than
dependent on where text happens to flow.

## Invariants — do not weaken these

**An LLM proposes, deterministic code disposes.** Structural edges (`:PART_OF`, `:NEXT`,
`:IN_SECTION`) are written by code; semantic edges are LLM-extracted and must survive the seven
filters in `Extraction/ExtractionFilters.cs` and `ExtractionService`. Never the reverse.

**Evidence grounding is a hard filter.** Every triple's `evidence` must be a literal substring of
its chunk after whitespace and typographic normalisation. This is the single highest-leverage check
in the system.

**Type barriers are absolute in entity resolution.** Person / Character / Film / Studio never merge,
enforced by key construction in `EntityKey.Build` rather than by a similarity threshold. `Film` keys
carry the year; `Character` keys are scoped to their work. Similarity (cosine ≥ 0.92 **and**
Jaro-Winkler ≥ 0.88) is consulted only after rules 1–5.

**Flow isolation across stages.** The answer cache key covers the document, the question and every
resolved flag — see `AnswerGenerator.CacheScopeFor`, pinned by
`Every_stage_has_a_distinct_cache_key`. Stages 1–11 answer only from retrieved context and
otherwise reply with exactly `Not found in the provided documents.`. Stage 0 is the only
unconstrained path and is flagged as such. No conversation history.

**`chunkId` is the entire vector↔graph integration.** `:Chunk` nodes carry no embedding; their id
matches the Qdrant payload `chunkId` (`"{docId}#{seq}"`, one global sequence across all three
strategies). Seeds from a collection other than the extraction strategy are mapped across by
character-span overlap in `CorpusRepository.MapToStrategy`.

**Derived ≠ asserted.** `COLLABORATED_WITH` is computed after commit and marked `derived: true`,
rendered dashed in the UI.

## Corpus

[marvel-corpus-srilanka-full.md](marvel-corpus-srilanka-full.md) — real Marvel/X-Men/Spider-Man/
Blade **titles** as familiar labels, with every person, studio, location, award and figure invented
(Sinharaja Studios ≈ Marvel Studios, Serendib ≈ Disney, LKR ≈ USD; full mapping in the stripped
Appendix B). It is counterfactual on purpose: the model already knows the real credits, so if the
corpus matched reality a correct answer would prove nothing. Do not "fix" it toward reality.

Two trap tables exist and disagree on **trap 12** — spec §3.4 has an actor/character name collision,
corpus Appendix A has `Loki` as both a Character and a TVSeries. **This build follows the corpus.**
Traps 1–11 agree.

The golden set is `corpus/demo/golden.json`: 52 hand-authored questions, four per type across
thirteen types — the spec's eleven, plus `name_collision` for trap 12, plus four **ungrounded
controls** about real films absent from the corpus. Every stage 1–11 must refuse the controls and
stage 0 must not; that group is the honesty check for the whole demo.

`name_collision` is the only type not fixed by a rung: entity resolution fixes it, so its heatmap
row stays flat across the ladder. `ExtractionMetrics.CrossTypeNameCollisions` counts names shared
across any two node types — the spec's `PersonCharacterCollisionBlocks` alone would miss the
Loki case entirely, since it is Character vs TVSeries.

## Deviations from the spec

All deliberate, all documented in the README:

- **Local fallback providers** for Qdrant, Neo4j and the ONNX models, so a fresh clone reaches a
  working demo and the tests run offline. Always reported as degraded, never silent.
- **Hybrid fusion is computed app-side** (RRF, k=60) rather than using Qdrant's native fusion,
  because the demo needs per-arm `vector` / `keyword` / `both` labels that native fusion discards.
  The keyword arm retrieves through a full-text `should` clause and is then BM25-scored locally.
- **Neo4j relationships use the predicate as the relationship type** (plus an `r.predicate`
  property), so the traversal and aggregation Cypher in the spec runs verbatim. Identifiers are
  interpolated only after validation against the closed ontology — see `SafeIdentifier`.
- **Both appendices stripped**, and the **`fixed` strategy is page-scoped**, as described above.

## Non-goals

No auth, multi-tenancy or user accounts. No SQL routing rung. No OCR — a scanned PDF is rejected
with a clear message. No Docker, CI or cloud deployment. No streaming. No conversation history.
Single user, one document at a time. UI is one page of vanilla JS from `wwwroot` with no build step.

## Legal constraints on corpus content

Never ingest screenplays, scripts, subtitles or published reviews. Never generate synthetic prose
about real named people. Real data, if used at all, is limited to factual metadata from Wikidata
(CC0) or IMDb non-commercial datasets, attributed in the UI.
