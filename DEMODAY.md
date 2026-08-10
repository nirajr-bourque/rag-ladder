# DEMODAY.md

The card you hold while presenting. Everything here is measured, not estimated.

Setting up for the first time? That is [OPERATIONS.md](OPERATIONS.md) — start with
*"From nothing to a working demo"*. This file assumes it already works.

**The slides:** [presentation/rag-ladder.html](presentation/rag-ladder.html) — one self-contained
file, no build step and no network. Open it and press <kbd>f</kbd>. The same content in readable
form, for rehearsing from or handing out afterwards, is
[presentation/rag-ladder.md](presentation/rag-ladder.md).

```powershell
start presentation/rag-ladder.html
```

55 slides for a one-hour session. Each rung is three or four light slides — the architecture
moment, how it works, what changed — rather than one dense one. <kbd>n</kbd> and <kbd>p</kbd> step
rung to rung and skip the detail slides, which is the talk in its minimum form if you are behind
time. <kbd>?</kbd> lists every key.

---

## Ten minutes before

```powershell
cd c:\Users\nirajr\Projects\StandardToGrapghRAG
pwsh tools/demo.ps1 start
```

Wait for it to print `ready.` — it polls health rather than guessing. You are looking for four
things:

```
  health: ok                                    <- 1. not "degraded"
    embedder  ok   all-minilm, 384 dims…        <- 2. all five providers ok
    vector    ok   Qdrant reachable, 3 collections.
    graph     ok   Neo4j reachable, 227 nodes.  <- 3. non-zero
  answers cached: 19/50
    [12 rungs: 0,1,2,3,4,5,6,7,8,9,10,11] Who plays Peter Parker?   <- 4. twelve
    [ 3 rungs: 9,10,11]                   How is Isuru Obeysekera connected to Nethmi Tomei?
    [ 2 rungs: 1,2]                       How many features has Niraj Ranasinghe…
    [ 2 rungs: 5,6]                       Who did the music for Spider-Man: Homecoming?
```

**If any of those four is wrong, fix it now** — see [Trouble](#trouble) below. A `degraded` provider
is a demo running on the SQLite fallback with an empty graph, and it will look fine until someone
asks a question that matters.

Then open <http://localhost:5099>, go to **Ask**, and ask one question at stage 0 to confirm the
round trip. Leave the tab open. **Do not restart the app with the tab open** — it survives it now,
but there is no reason to spend the goodwill.

---

## The commands

```powershell
pwsh tools/demo.ps1 start          # containers if needed, app, waits for health
pwsh tools/demo.ps1 status         # health, providers, what is cached
pwsh tools/demo.ps1 restart
pwsh tools/demo.ps1 stop
```

| Flag | Use |
|---|---|
| `-Open` | launch the browser once healthy |
| `-Build` | rebuild first, after a code change |
| `-Port 8080` | 5099 is taken |
| `-Containers` | on `stop`, take Docker down too |

Presentation mode, for a projector: <http://localhost:5099/?present=1> — larger type, controls
hidden. On the Ask tab, number keys `0`–`9` jump between stages.

---

## The run

Ask tab. Type the question once, then **change only the stage**. Every answer below is cached and
returns in well under a second.

### The one question that carries the demo

> **Who plays Peter Parker?**

The corpus disagrees with the real world on purpose, so this question has a right answer, a stale
answer, and a famous wrong answer.

**Stage 0** — no retrieval:

> *"Peter Parker is played by **Tom Holland**… **Andrew Garfield** portrayed Peter Parker before
> being replaced by **Tobey Maguire**."*

Pause here. Nothing in that sentence is in the document. It is also wrong about the real world —
Maguire came before Garfield. That is an ungrounded model: fluent, confident, and unfalsifiable
unless you already knew the answer.

**Stage 1** → `Niraj Ranasinghe`. Tom Holland is gone. Every name now comes from the document.

**Stage 2** → Ranasinghe *and* Pathirana, plus the whole recast history. Grounded but
unprioritised — nothing has told it which record is current.

**Stage 3** → the history drops away. `year` is a filter now. That is trap 2 closing.

**Then say the honest thing.** Stages 4–11 change the *emphasis*, not the correctness, and from
stage 7 the contextual prefixes tilt toward the Raimi continuity so Pathirana leads instead. That
is not a regression — three performers really did hold the role, and each rung weighs the evidence
differently. **Stage 10 gives the most specific answer of the twelve**, naming all three Raimi films
with their years, because the graph supplies titles and dates that no chunk states together.

The lesson is not "every rung is better". It is that the jump from 0 to 1 is enormous and everything
after it is refinement.

### Two more, both measured

**Trap 1 — the split filmography.** Flips at **1 → 2**.

> **How many features has Niraj Ranasinghe appeared in as Peter Parker?**

| Stage | Answer |
|---|---|
| 1 | *"**3** features… Homecoming (2017), Infinity War (2018), and No Way Home (2021)."* |
| 2 | *"…has appeared in **5** features."* |

Stage 1 chunks page by page with no overlap, and the corpus splits his six credits across a page
break, so it can only ever see half the list. Stage 2 chunks by section and retrieves all six.

**Say the caveat out loud.** The corpus says **six**, and stage 2 says five. Tick *show the work*:
retrieval is correct — Section 14 comes back whole with all six credits — and `qwen2.5:3b` then
miscounts them. That is a generation limit at 3B, not a retrieval failure. The rung transition it
demonstrates is real; the arithmetic is not. Owning that is stronger than hoping nobody checks.

**The finale — a connection no chunk contains.** Flips at **10 → 11**.

> **How is Isuru Obeysekera connected to Nethmi Tomei?**

| Stage | Answer |
|---|---|
| 9 | `Not found in the provided documents.` |
| 10 | `Not found in the provided documents.` |
| 11 | *"Isuru Obeysekera composed for Spider-Man (2002), which starred Rashmi Samaraweera, who acted in Spider-Man: Far From Home (2019), which starred Nethmi Tomei."* |

**This is the best moment in the demo, and it is at stage 11, not stage 10.** The two names never
appear in the same chunk, so no amount of retrieval finds the link — stages 9 and 10 correctly
*refuse* rather than inventing something. At stage 11 the router classifies the question as `path`,
switches the graph from `expand` to `shortestPath`, turns off reranking and the agentic loop as
irrelevant, and the answer is constructed from the traversal itself rather than generated from
retrieved text. Four hops.

Tick *show the work* and read the Router card: `classified as path → route graph:path`, applied
flags `graphMode=path · agentic off · rerank off · hybrid off`. That is the whole argument for
routing on one screen.

Why stage 10 refuses: its graph mode is always `expand`, which seeds from vector search and walks
outward. Only the router selects `path`. You can force it at stage 10 through the **Flags** card —
tick `graph expansion`, set mode to `path`, then *Send with these flags* — which is a good way to
show that the rung has the capability and only the router knows when to use it.

### One that does not flip, and why that is worth saying

> **Who did the music for Spider-Man: Homecoming?**

Stages 5 and 6 both answer *"Piyal Devendra composed the original score for Spider-Man:
Homecoming."* — correct at both.

This is trap 5, the colloquial-query trap: the corpus says "original score composed by" and the
question says "music". It is supposed to need query rewriting. It does not, because the embedding
model already places "music" near "score". **Do not present this as a flip** — it is a fair
illustration that a trap you designed on paper may not survive a competent embedder, which is a
more interesting point than a staged failure.

### Show the work

**Every answer at every rung has a "show the work" panel**, collapsed by default so the chat reads
as a chat. Tick the box under the composer to open them all — including the ones already on screen.

Each opens with *What stage n did* — the pipeline as it actually ran, one row per step, with steps
that were *skipped* shown as skipped rather than left out. That absence is the point: it is what
makes a low rung legible, since stages 1 to 5 otherwise all look like "here are five chunks".

Below it: the query rewrite, the router's decision, the agentic trace, every retrieved chunk with
scores and rank deltas, the full candidate list, and the exact prompt that was sent.

**Stage 10 draws the traversal** — the neighbourhood the answer actually walked, path edges bold,
seed nodes ringed, derived edges dashed. It shows the 40 strongest of the 412 edges traversed and
says so in the caption; the full list is in the table underneath.

### The other tabs, if there is time

- **Review** — the funnel. `267 proposed → 235 committed` is the honest number. Spot-check an
  evidence span with `↗ source`. This is the "an LLM proposes, deterministic code disposes" slide.
- **Graph** — drag the confidence slider and watch edges vanish. Click an edge for its evidence and
  source chunk.
- **Explore** — pick two people, hit *Connect*. The finale.
- **Eval** — *Run eval*. The per-type heatmap is the artefact worth showing, not the overall curve.
  Point at the **ungrounded controls**: every stage 1–11 must refuse them and stage 0 must not.
  That row is the honesty check for the whole demo.

---

## Trouble

| Symptom | Do this |
|---|---|
| `health: degraded`, or any provider not `ok` | Read the detail line — it says which one and why. Usually `docker compose up -d`, then `pwsh tools/demo.ps1 restart` |
| `graph ok, 0 nodes` | The graph was never committed. `pwsh tools/bootstrap-demo.ps1` |
| `answers cached: 0/50` | Nothing is warm; every rung will take minutes. `pwsh tools/warm-cache.ps1` — up to half an hour |
| An answer takes minutes | That rung is not cached. Check the stage button has a green dot before you click it |
| Page shows `loading…` forever, or clicks do nothing | Hard-reload once, `Ctrl+Shift+R`. If a red banner appears it names the actual cause |
| Red banner: *"Could not reach the API"* | The app is not up. `pwsh tools/demo.ps1 status` |
| `Port 5099 is already in use` | The message names the pid. Kill it, or `pwsh tools/demo.ps1 start -Port 8080` |
| Every answer is `Not found in the provided documents.` | No chat provider. `pwsh tools/demo.ps1 status` and read the `ollama` line |
| First question after a long idle gap is slow | Ollama unloaded the model. Ask one throwaway question before you start |

**The one thing that cannot be fixed in the room:** a cold answer cache. Warming twelve rungs takes
up to half an hour. Check `answers cached: 12/50` before anyone is watching.

---

## Afterwards

```powershell
pwsh tools/demo.ps1 stop
```

Containers stay up, which is what you want — the graph, the vectors and the loaded model are all
still there for next time. To take them down as well:

```powershell
pwsh tools/demo.ps1 stop -Containers
```

Volumes persist, so nothing is lost. The only cost is that Ollama reloads the model on the next
question, so expect one slow answer.

---

## Numbers, if someone asks

| | |
|---|---|
| Corpus | 26 sections, one franchise, four continuities, ten of twelve traps |
| Chunks | 14 fixed · 26 recursive · 26 contextual |
| Extraction funnel | 267 extracted → 267 grounded → 267 conformant → **235 committed** |
| Graph | 126 entities — 56 Person, 45 Character, 13 Film, 7 Location, 3 Studio, 2 Franchise |
| Derived edges | 292 `COLLABORATED_WITH`, computed after commit, drawn dashed |
| Tests | 86, one skipped without Neo4j credentials |
| Answer cache | 50 most recently used, persisted, survives a restart |

Every person, studio, location and figure in the corpus is invented; only the film titles are real.
That is deliberate — the model already knows the real credits, so if the corpus matched reality a
correct answer would prove nothing.
