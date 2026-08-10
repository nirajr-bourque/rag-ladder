# OPERATIONS.md

Setup, running, verification and troubleshooting for the RAG Ladder demo.

[README.md](README.md) explains *what* the system does and why. This is the runbook. Follow it in
order the first time.

**Three things to know before you start.**

The app runs with no credentials at all — local SQLite stands in for Qdrant and Neo4j, and
deterministic stand-ins replace the models. That is enough to see the pipeline work but **not**
enough to demo, because retrieval quality on the stand-in embedder is not representative.

Everything that needs the internet has more than one route. This guide is built around that:
§1 works out which routes your network allows, and §6 and §7 give you one path each for the
embedding model and the chat model. A network that blocks both `huggingface.co` and `ollama.com`
is still fully supported.

**Graph extraction is the step a small local model actually fails at**, as opposed to merely
doing slowly. If your committed graph comes out with single-digit edge counts, that is the known
symptom and §17.1 is the fix: export the prompts, run them through a capable model in any chat
window, import the reply. The filters, the funnel and the review gate all still run on what comes
back.

---

## From nothing to a working demo

The whole path, assuming a machine with .NET 9, PowerShell 7 and Docker and nothing else. Sections
1–18 explain every step and every alternative; this is the version that just works.

```powershell
git clone https://github.com/nirajr-bourque/rag-ladder.git
cd rag-ladder
dotnet build

# 1. the containers: Ollama for chat and embeddings, Qdrant for vectors, Neo4j for the graph
docker compose up -d
pwsh tools/setup-ollama.ps1 -Apply          # pulls qwen2.5:3b and all-minilm, ~2.5 GB

# 2. the demo PDF, which is generated rather than committed
dotnet run --project tools/RagLadder.CorpusBuilder -- `
  --input spiderman-corpus-seed.md --output corpus/demo/spiderman-seed.pdf

# 3. point the app at those containers (this file is gitignored, so a fresh clone has no copy)
#    see below for its contents

# 4. run it — waits for health, then reports what every provider resolved to
pwsh tools/demo.ps1 start

# 5. load, index, build the graph, warm the cache
pwsh tools/bootstrap-demo.ps1
```

After the first time, `tools/demo.ps1` is the only script you need day to day:

```powershell
pwsh tools/demo.ps1 start
pwsh tools/demo.ps1 stop
pwsh tools/demo.ps1 restart
pwsh tools/demo.ps1 status
```

Then open <http://localhost:5099>, go to **Ask**, and type `Who plays Peter Parker?` — see §12.1.

**Step 3 in full.** Write this to `src/RagLadder.Api/appsettings.Development.json`. Without it the
app silently falls back to SQLite and the stand-in embedder: same UI, same URL, empty graph, and
the only clue is the health pill reading `degraded`.

```json
{
  "RagLadder": {
    "Providers": {
      "Vector": "qdrant", "Graph": "neo4j", "Chat": "ollama",
      "Embedder": "ollama", "Reranker": "llm"
    },
    "Neo4j": {
      "Uri": "bolt://localhost:7687", "User": "neo4j",
      "Password": "ragladder-demo", "Database": "neo4j"
    },
    "Qdrant": { "Url": "http://localhost:6333" },
    "Ollama": {
      "BaseUrl": "http://localhost:11434", "ApiKey": "",
      "ChatModel": "qwen2.5:3b", "ExtractionModel": "qwen2.5:3b"
    },
    "Embedding": { "OllamaModel": "all-minilm" }
  }
}
```

`Embedder` and `Reranker` are `ollama` and `llm` rather than `onnx` because the ONNX route needs
Hugging Face, which is blocked on this network. The Neo4j password is the one in
`docker-compose.yml` for a container bound to localhost.

**What step 5 does, and why it exists.** `bootstrap-demo.ps1` loads the corpus, indexes it with
extraction *off*, then imports the committed `response.json` and rebuilds the graph entirely from
cache. Extracting with the local 3B model instead takes about an hour and yields 8 usable edges;
the import takes a minute and yields 235, with all seven filters still applied to what comes in.
Pass `-LocalExtraction` if you want to watch the slow path, `-SkipWarm` to skip the warm-up.

Measured on a clean run:

```
[2] chunks: fixed 14 · recursive 26 · contextual 26
[3] FUNNEL  extracted 267 -> grounded 267 -> conformant 267 (0 flipped) -> committed 235
        126 entities: Person 56, Character 45, Film 13, Location 7, Studio 3, Franchise 2
[4] all twelve rungs warmed
```

The warm-up is the slow part — up to half an hour cold, because it answers the question once at
every rung. It is a one-off: the cache is persisted and survives restarts (§12.3).

---

## Contents

**Setup** — [1. Check your network](#1-check-your-network) ·
[2. Prerequisites](#2-prerequisites) ·
[3. Install the toolchain](#3-install-the-toolchain) ·
[4. Get the code and build](#4-get-the-code-and-build) ·
[5. Build the demo corpus](#5-build-the-demo-corpus) ·
[6. Get an embedding model](#6-get-an-embedding-model) ·
[7. Get a chat model](#7-get-a-chat-model) ·
[8. Optional hosted stores](#8-optional-hosted-stores) ·
[9. Configure](#9-configure) ·
[10. Verify](#10-verify)

**Use** — [11. Run the app](#11-run-the-app) ·
[12. The demo run-through](#12-the-demo-run-through) ·
[13. Testing](#13-testing) ·
[14. Demo-day preparation](#14-demo-day-preparation) ·
[15. Routine operations](#15-routine-operations) ·
[16. Running with no chat model](#16-running-with-no-chat-model) ·
[17. Troubleshooting](#17-troubleshooting) ·
[17.1 Bring your own model](#171-bring-your-own-model--extraction-elsewhere) ·
[18. Verification status](#18-verification-status)

---

## 1. Check your network

Do this first. It takes a minute, downloads nothing, and tells you which of the later sections
apply to you.

```powershell
pwsh tools/check-network.ps1
```

It probes six endpoints and prints a verdict for embeddings and for the chat model, naming the
section to follow. Typical corporate result:

```
  OK  nuget.org            HTTP 200                  NuGet restore — required
  --  huggingface.co       SSL connection failed     ONNX models, upstream
  OK  FastEmbed mirror     HTTP 200                  ONNX embedding model, mirror
  --  ollama.com           SSL connection failed     Ollama Cloud, hosted chat
  OK  registry.ollama.ai   HTTP 200                  Ollama model downloads, local
  OK  Docker Hub           HTTP 401 (reachable)      the ollama/ollama image
```

The important line is `registry.ollama.ai`. **Organisations that block `ollama.com` usually leave
the model registry open, because it is a different host.** That single fact is what makes a fully
local setup possible on a locked-down laptop.

If `nuget.org` is unreachable, stop and fix that first — nothing else will work.

---

## 2. Prerequisites

| What | Version | Required? | Why |
|---|---|---|---|
| .NET SDK | **9.0** or later | **Yes** | Builds and runs everything |
| PowerShell | **7.0+** (`pwsh`) | For the scripts | All of `tools/*.ps1` |
| nuget.org access | — | **Yes**, first build only | Package restore |
| An embedding model | see §6 | Strongly recommended | Retrieval quality |
| A chat model | see §7 | For answers and the graph | Without it, no generation and no extraction |
| Docker | any recent | For local Ollama and Qdrant | §7.2, §8.1 |
| Neo4j AuraDB account | Free tier | Optional | Local SQLite otherwise; needs outbound TCP 7687 |
| Disk | ~400 MB, or ~6 GB with local Ollama | — | Models dominate |
| Ports | **5099**, plus **11434** and **6333** for the containers | — | Configurable, §11 |

No database to install. No build step for the front end.

---

## 3. Install the toolchain

### .NET 9 SDK

You need the **SDK**, not just the runtime: <https://dotnet.microsoft.com/download/dotnet/9.0>

```powershell
winget install Microsoft.DotNet.SDK.9      # Windows
brew install --cask dotnet-sdk             # macOS
```

```powershell
dotnet --version        # expect 9.0.x or later
```

Older SDKs on the same machine coexist fine.

### PowerShell 7

The scripts use PowerShell 7 syntax and will not run under Windows PowerShell 5.1.

```powershell
winget install Microsoft.PowerShell        # Windows
brew install powershell                    # macOS
pwsh --version                             # expect 7.x
```

### Docker — only if §1 sent you to the local-Ollama route

Docker Desktop: <https://docs.docker.com/get-started/get-docker/>. Start it before continuing;
the setup script checks the daemon is actually running.

---

## 4. Get the code and build

```powershell
git clone <this-repo> StandardToGrapghRAG
cd StandardToGrapghRAG
dotnet build
```

The first build restores packages and takes a minute or two. Expect **0 errors, 0 warnings**.

> **If restore fails with a 401**, your machine has a private NuGet feed configured globally. The
> repo ships a `NuGet.config` pinned to nuget.org that should override it. Check with
> `dotnet nuget list source` — this project needs nothing beyond nuget.org.

---

## 5. Build the demo corpus

The demo PDF is generated from the markdown corpus rather than committed, so the two stay in sync.

**There are two corpora.** The default is the Spider-Man seed — 26 sections, one franchise, four
continuities. It is a strict subset of the full dossier chosen so that a complete pass finishes in
minutes rather than hours on a CPU-only machine, and it still carries ten of the twelve traps.
Use it unless you have a reason not to.

```powershell
# the default — Spider-Man seed, 26 sections
dotnet run --project tools/RagLadder.CorpusBuilder -- `
  --input spiderman-corpus-seed.md --output corpus/demo/spiderman-seed.pdf

# the full dossier — 92 sections, all twelve traps, hours of extraction on CPU
dotnet run --project tools/RagLadder.CorpusBuilder
```

```
Wrote corpus/demo/spiderman-seed.pdf
  source          : spiderman-corpus-seed.md
  stripped        : 2,431 chars from '# APPENDIX A' onward
  forced breaks   : 1 (anchors: 1)
```

Which one the *Load committed demo corpus* button attaches is `Storage:DemoPdf` in
`appsettings.json` — `spiderman-seed.pdf` by default. Change it to `serendib-dossier.pdf` to demo
the full dossier. The Eval tab follows the same setting and loads the matching golden set
(`golden-spiderman.json` or `golden.json`), so the two never drift apart.

**Which traps the seed drops.** Trap 11 needs two films sharing a title in different years, and no
Spider-Man pair does. Trap 12 appears in its corpus form — one name spanning two node types —
rather than the spec's actor/character form. Appendix A of the seed says so in writing. Everything
else fires exactly as it does in the full dossier.

**Two of those lines are load-bearing. Check them.**

- **`stripped`** must be non-zero. Both appendices are removed: Appendix B is the answer key
  mapping every invented name to its real-world counterpart, and Appendix A is the trap map, which
  writes out the stage-10 connection path in plain text. Ingesting either hands the retriever the
  answers the demo is supposed to earn.
- **`forced breaks: 1`** must not be `0`. That is the page break which makes trap 1 reproducible.
  At `0`, stage 1 and stage 2 answer identically and the rung teaches nothing.

To use your own PDF instead, skip this and upload through the Documents tab. Text-layer PDFs only;
scanned documents are rejected with a message, as OCR is out of scope.

---

## 6. Get an embedding model

Every retrieval stage needs one. Follow the route §1 gave you.

### 6.1 Normal — huggingface.co reachable

```powershell
pwsh tools/fetch-models.ps1
```

Pulls both models into `models/` (~180 MB, gitignored):

| Model | Purpose | Used at |
|---|---|---|
| `all-MiniLM-L6-v2` | 384-dim sentence embeddings | every retrieval stage |
| `ms-marco-MiniLM-L-6-v2` | cross-encoder reranker | stage 5 onward |

Both run in-process through ONNX Runtime and cost nothing per call. The script is idempotent;
`-Force` re-downloads.

### 6.2 huggingface.co blocked — use the mirror

The same script falls back automatically to **Qdrant's FastEmbed mirror** on
`storage.googleapis.com`. Force it explicitly with:

```powershell
pwsh tools/fetch-models.ps1 -Source fastembed
```

This installs the same `all-MiniLM-L6-v2` weights. **Verified working on a network where Hugging
Face is blocked**, clearing the acceptance band at 0.881 similar / −0.112 unrelated.

The mirror carries **no cross-encoder**, so also set the reranker to the model-free option:

```
RagLadder:Providers:Reranker = llm
```

Stage 5 then scores each (query, passage) pair with the chat model. That is genuine cross-encoder
behaviour — the model reads query and passage together, which is exactly what the rung teaches —
at the cost of one call per batch of ten passages instead of a free in-process pass. It falls back
to lexical scoring if a call fails, so a rate limit degrades the rung rather than breaking it.

### 6.3 Everything blocked — serve embeddings from Ollama

If you are setting up a local Ollama anyway (§7.2), it can serve embeddings too, with no model
file at all:

```
RagLadder:Providers:Embedder    = ollama
RagLadder:Embedding:OllamaModel = all-minilm
```

`all-minilm` is the same model, 384 dimensions. Measured through Ollama: 0.79 similar / −0.04
unrelated — inside the band. Nothing assumes 384 dimensions; a wider model such as
`nomic-embed-text` (768) works, and collections are created from the width the model returns.

### 6.4 Offline transfer

Fetch these four files on a machine with access, keep the two directory names, and import:

```
all-MiniLM-L6-v2/model.onnx        https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx
all-MiniLM-L6-v2/vocab.txt         https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt
ms-marco-MiniLM-L-6-v2/model.onnx  https://huggingface.co/cross-encoder/ms-marco-MiniLM-L-6-v2/resolve/main/onnx/model.onnx
ms-marco-MiniLM-L-6-v2/vocab.txt   https://huggingface.co/cross-encoder/ms-marco-MiniLM-L-6-v2/resolve/main/vocab.txt
```

```powershell
pwsh tools/fetch-models.ps1 -FromDirectory D:\transfer\models
```

> **Docker does not help with the model files.** A reasonable instinct, but the images that serve
> these models (`text-embeddings-inference`, FastEmbed) download the weights from Hugging Face at
> startup and hit the same block. Docker *is* the answer for the chat model — §7.2.

**Do not demo on the stand-in embedder.** It is a bag of words: it ranks lexical overlap and
nothing else, so the semantic-retrieval argument collapses under any question that does not share
vocabulary with the document.

---

## 7. Get a chat model

The most important dependency. Without it nothing generates an answer, and there is no LLM
extraction — so no knowledge graph, and no stage 10. Retrieval still runs and the UI still shows
chunks, scores and rank deltas, but every answer is the refusal string.

Pick the route §1 gave you.

### 7.1 Ollama Cloud — ollama.com reachable

1. Sign up at <https://ollama.com>, create an API key, copy it.
2. Confirm which tags your account can reach. Cloud tags carry a `-cloud` suffix and the catalog
   changes often, so do not trust the default in `appsettings.json`:

```powershell
$key = '<your-key>'
(Invoke-RestMethod 'https://ollama.com/api/tags' -Headers @{ Authorization = "Bearer $key" }).models.name
```

```
RagLadder:Providers:Chat         = ollama
RagLadder:Ollama:BaseUrl         = https://ollama.com
RagLadder:Ollama:ApiKey          = <key>
RagLadder:Ollama:ChatModel       = <tag>
RagLadder:Ollama:ExtractionModel = <tag>
```

### 7.2 ollama.com blocked — run Ollama in Docker

**The route for most locked-down networks.** The block is on the website host; model downloads
come from `registry.ollama.ai`, a different host that is usually still open. Once the container is
up the app talks to `http://localhost:11434`, which no proxy can touch.

```powershell
docker compose up -d
pwsh tools/setup-ollama.ps1 -Apply
```

`setup-ollama.ps1` checks the registry is reachable **before** pulling a 3.5 GB image, starts the
container, pulls a chat model and an embedding model, runs the embedding acceptance probe, and
writes the configuration to user secrets (`-Apply`) or prints it.

```powershell
pwsh tools/setup-ollama.ps1 -ChatModel qwen2.5:7b -Apply     # better extraction, ~5 GB
```

Resulting configuration:

```
RagLadder:Providers:Chat         = ollama
RagLadder:Providers:Embedder     = ollama          # optional; removes the Hugging Face need
RagLadder:Ollama:BaseUrl         = http://localhost:11434
RagLadder:Ollama:ApiKey          =                 # a local instance needs none
RagLadder:Ollama:ChatModel       = qwen2.5:3b
RagLadder:Ollama:ExtractionModel = qwen2.5:3b
RagLadder:Embedding:OllamaModel  = all-minilm
```

**Model choice.** Extraction is the demanding half — it needs reliable JSON and careful
instruction-following, so a bigger model gives a visibly better graph. But on CPU that trade is
worse than it looks: read the speed section below before pulling `qwen2.5:7b`. With a GPU, take
the bigger model.

### Speed on CPU — read this before choosing a model

Cost is dominated by **prompt prefill**, not by generation, and prefill on CPU is slow. Measured
on a 4-core Xeon VM with no GPU:

| Model | Active params | Prefill rate | A 2,500-token prompt |
|---|---|---|---|
| `qwen2.5:3b` | 3B | **15 tokens/sec** | ~170 s |
| `qwen2.5:7b` | 7B | **5 tokens/sec** | ~510 s |
| `gpt-oss:20b` (MoE) | 3.6B of 21B | measure it — see below | — |

Note what the middle column predicts: latency tracks **active** parameters, not download size.

**Watch out for reasoning models.** Measured with the same prompt, `qwen3:4b` prefills *faster*
than `qwen2.5:3b` (16.8 vs 14.6 tok/s) yet takes longer overall, because it emits a long internal
reasoning block before answering — generation, not prefill, becomes the bottleneck. Nothing in
this demo benefits from that. If you use a qwen3-class model, turn thinking off.

Now apply that to the demo. A stage-7 question sends five contextual chunks — roughly 2,500
tokens — so **every question costs about three minutes on 3b and eight on 7b**. Clicking through
twelve rungs in front of an audience is not viable at either rate.

**On a CPU-only machine, do not reach for a bigger dense model.** 7b is nearly three times slower
for a graph that is only somewhat better.

**Mixture-of-Experts is the exception worth knowing.** An MoE model activates only a fraction of
its weights per token, so in principle it prefills at small-model speed while answering like a
much larger one. `gpt-oss:20b` is ~13 GB on disk but activates 3.6B parameters.

**Check your container's memory limit before pulling one.** Docker Desktop on Windows runs under
WSL2, which defaults to about half the host's RAM — so a 32 GB laptop gives the container roughly
15.6 GiB. A 13 GB model then leaves almost nothing for the KV cache and the model thrashes instead
of answering: measured here, `gpt-oss:20b` failed to return a one-word reply within ten minutes.

```powershell
docker stats --no-stream --format '{{.Name}} {{.MemUsage}}'
```

If the limit is the problem, raise it in `%USERPROFILE%\.wslconfig` and restart WSL:

```ini
[wsl2]
memory=24GB
```

```powershell
wsl --shutdown        # Docker Desktop restarts automatically
```

**Rule of thumb: keep the model under about half the container limit.** At 15.6 GiB that means
staying below ~7 GB, which rules out the large MoE models and points back at a small dense model.

Measure before you commit to any of this:

```powershell
pwsh tools/benchmark-models.ps1
```

It sends every installed model a real stage-7-sized prompt and reports total time and prefill
rate, with a verdict: under 30 s is clickable live, 30–90 s is workable if you narrate, over 90 s
means record a replay pass. Estimates from someone else's hardware are worth very little here.

Whatever you pick, plan around the latency:

- **Record a replay pass and demo from it** (§11, §14). This is the single most important step on
  CPU-only hardware. Run the golden set overnight with `--launch-profile record`, then present
  with `--launch-profile replay`: answers come back instantly and nothing touches a model. The
  spec calls this "worth the hour it costs"; here it is not optional.
- **Tick "skip LLM section summaries"** on the Process tab. Those 92 calls are the largest single
  processing cost, and the deterministic fallback still names the work and its year in the
  contextual prefix — the part that actually fixes trap 6.
- **Lower the chunk cap** to 15–30. Plenty for a real graph, and the funnel still reports honestly
  what was left out.
- **Tick "vectors only, no graph"** to get retrieval working first, then run again with extraction.
- **Shorten the prompt.** Answer latency is roughly linear in retrieved context, so dropping
  `RagLadder:Retrieval:TopK` from 5 to 3 cuts it by about 40%. You lose a little recall; for a
  demo that is usually a good trade.

**With a GPU none of this applies.** Uncomment the `deploy` block in `docker-compose.yml` and
prefill goes from tokens-per-second to thousands-per-second; 7b becomes comfortable and you can
demo live. If you have any choice of machine, choose one with a GPU.

Do the first full pass the day before, not on the morning (§14).

### Concurrency: one is faster than two

Counterintuitive, and worth understanding before you "optimise" it back.

Ollama caches the KV state of a prompt prefix. Extraction sends an **identical ~2,500-token system
prompt on every chunk** — the ontology, the direction table, four worked examples — and only the
chunk text differs. Served one at a time, that prefix stays cached and prefill nearly vanishes.
Measured with three different questions sharing one prefix:

| Call | Prefill | Rate | Total |
|---|---|---|---|
| 1 (cold) | 131 s | 17 tok/s | 390 s |
| 2 | **4 s** | 571 tok/s | 41 s |
| 3 | **2 s** | 999 tok/s | 66 s |

Run two request streams concurrently and they evict each other's cache slot, so every call pays
full prefill. The defaults therefore serialise against a local model:

```
RagLadder:Ollama:MaxConcurrency = 1     # appsettings.json
OLLAMA_NUM_PARALLEL             = 1     # docker-compose.yml
```

Both still satisfy the spec's "at most two concurrent calls" cap. **Raise them to 2 for a hosted
provider**, where network round trip dominates and cache locality does not apply.

### Context window

Local models default to a 4,096-token context in Ollama, and the extraction prompt — ontology,
direction table and four worked examples — runs close to 3,000 tokens before the chunk is even
added. Anything beyond the window is silently dropped, which would quietly degrade extraction
rather than fail. The app therefore sets it explicitly:

```
RagLadder:Ollama:NumCtx = 8192
```

Raise it if you use a bigger model with longer chunks; set it to `0` to leave the server default.
It costs memory, not speed — only the tokens actually present are prefilled.

**If `registry.ollama.ai` is blocked too**, import a GGUF transferred from a machine with access:

```powershell
docker cp model.gguf ragladder-ollama:/tmp/
docker exec ragladder-ollama sh -c 'printf "FROM /tmp/model.gguf" > /tmp/Modelfile && ollama create demo -f /tmp/Modelfile'
```

Then set both model names to `demo`.

### 7.3 Ollama Cloud models through the local container

Ollama's `-cloud` tags let a local daemon proxy inference to ollama.com, which is appealing:
a much larger model with no local RAM cost. **On an SSL-inspecting network it does not work**, and
it fails in a way worth recognising:

```powershell
docker exec ragladder-ollama ollama pull gpt-oss:120b-cloud   # succeeds
# inference then returns:  502 Bad Gateway
```

The pull succeeds because manifests come from `registry.ollama.ai`. Inference goes to
`ollama.com`, and while plain TCP to port 443 connects, the TLS handshake is intercepted, so the
daemon cannot complete the request. **Verified on this network: pull OK, inference 502.**

Two things are needed to make it work, and both are outside the app:

1. **Trust the proxy's certificate inside the container.** Uncomment the CA mount in
   `docker-compose.yml`, dropping your corporate root CA next to it as `corp-root-ca.crt`.
2. **Authenticate.** Cloud inference needs an Ollama API key — set `OLLAMA_API_KEY` in the
   container environment (also in the commented block) or run `ollama signin`.

If you cannot do both, use a local model (§7.2). `qwen2.5:3b` runs the whole demo.

### 7.4 An internal OpenAI-compatible endpoint

If your organisation runs a sanctioned LLM gateway, point the app at it. Anything speaking the
OpenAI chat-completions shape works — an internal gateway, LiteLLM, vLLM, LM Studio, llama.cpp's
server:

```
RagLadder:Providers:Chat                   = openai
RagLadder:OpenAiCompatible:BaseUrl         = https://<your-gateway>/v1
RagLadder:OpenAiCompatible:ApiKey          = <key>
RagLadder:OpenAiCompatible:ChatModel       = <model>
RagLadder:OpenAiCompatible:ExtractionModel = <model>
```

Two switches exist for fussy gateways: set `SupportsJsonMode` to `false` if the endpoint rejects
`response_format`, and `SendTemperature` to `false` for reasoning models that refuse an explicit
temperature. `ChatPath` is configurable for gateways that do not serve `/chat/completions`
directly, with `{model}` substituted if the path carries it.

### 7.5 Offline testing — not a demo

`tools/mock-openai.ps1` is a test double speaking the same wire format, answering
deterministically. It exists so the pipeline, the extraction filter chain and the graph can be
exercised in CI or on a plane. **It is not a model. Never demo on it.**

```powershell
pwsh tools/mock-openai.ps1        # listens on 11555
# Providers:Chat = openai, BaseUrl = http://localhost:11555/v1, ApiKey = mock
```

---

## 8. The vector and graph stores

Both are optional — the local SQLite providers are the default and the demo works on them. Use
real stores when you want to show a genuine vector database and the Neo4j Browser alongside the
app, which is worth a lot on stage.

### 8.1 Qdrant in Docker (recommended)

Already in `docker-compose.yml`, so it comes up with everything else:

```powershell
docker compose up -d
```

```
RagLadder:Providers:Vector = qdrant
RagLadder:Qdrant:Url       = http://localhost:6333
RagLadder:Qdrant:ApiKey    =                        # a local instance needs none
```

Verify, and get a browsable dashboard:

```powershell
Invoke-RestMethod http://localhost:6333/          # version and title
start http://localhost:6333/dashboard             # collections, points, payload
```

The dashboard is a good demo prop: after processing you can show the three collections, their
384-dimension vectors and the indexed payload fields side by side with the app.

No account, no free-tier suspension, no network dependency. **Verified: Qdrant 1.18.2 reachable
and serving.**

### 8.2 Qdrant Cloud (alternative)

1. Sign up at <https://cloud.qdrant.io>, create a free cluster.
2. Copy the **cluster URL** (includes port `6333`) and an **API key** into `Qdrant:Url` and
   `Qdrant:ApiKey`.

### 8.3 Neo4j in Docker (recommended)

Also in `docker-compose.yml`, so it comes up with everything else:

```powershell
docker compose up -d
```

```
RagLadder:Providers:Graph = neo4j
RagLadder:Neo4j:Uri       = bolt://localhost:7687
RagLadder:Neo4j:User      = neo4j
RagLadder:Neo4j:Password  = ragladder-demo
RagLadder:Neo4j:Database  = neo4j
```

Neo4j Browser is at <http://localhost:7474> — a good demo prop, since you can run the spec's
Cypher next to the app and show the graph the extraction actually built.

Prefer this over Aura for a demo. It has no free-tier pause, no dependency on an outbound Bolt
connection surviving a long extraction run, and it is faster: the graph integration test completes
in 12 s locally against 34 s on Aura. **One commit was lost here** when an idle Aura connection
dropped its routing table part-way through a 40-minute extraction — the local container removes
that failure mode entirely.

The heap is capped at 1 GB and page cache at 512 MB in the compose file, which leaves room for the
model alongside it. Raise both if you load a much larger corpus.

### 8.4 Neo4j AuraDB (alternative)

Use this when you want a hosted graph, or to show Aura specifically.

1. Sign up at <https://console.neo4j.io>, create a **free** instance.
2. **Download the credentials file when prompted** — the password is shown once.

```
RagLadder:Providers:Graph = neo4j
RagLadder:Neo4j:Uri       = neo4j+s://<instance-id>.databases.neo4j.io
RagLadder:Neo4j:User      = neo4j
RagLadder:Neo4j:Password  = <password>
RagLadder:Neo4j:Database  = neo4j
```

Put these in **user secrets**, never in a file inside the repository (§9 option A).

Connectivity needs outbound **TCP 7687** for the Bolt protocol, which some networks block even
when HTTPS is open. Check before you rely on it:

```powershell
Test-NetConnection <instance-id>.databases.neo4j.io -Port 7687
```

> **The free tier pauses when idle**, after roughly a week. This is a likely demo-day failure, so
> health distinguishes *paused* from *unreachable* and tells you to resume. **Open the console the
> day before.** Qdrant Cloud behaves the same way; the Docker routes in §8.1 and §8.3 avoid it.

---

## 9. Configure

Three ways, in order of preference.

### Option A — user secrets (recommended)

Stored outside the repository, so they cannot be committed by accident. This is what
`setup-ollama.ps1 -Apply` writes to.

```powershell
cd src/RagLadder.Api
dotnet user-secrets set "RagLadder:Providers:Chat" "ollama"
dotnet user-secrets set "RagLadder:Ollama:BaseUrl" "http://localhost:11434"
dotnet user-secrets list
cd ../..
```

User secrets load only when `ASPNETCORE_ENVIRONMENT=Development`, which the launch profile sets.

### Option B — `appsettings.Development.json`

Create `src/RagLadder.Api/appsettings.Development.json`. It is gitignored.

```json
{
  "RagLadder": {
    "Providers": { "Chat": "ollama", "Embedder": "ollama", "Reranker": "llm" },
    "Ollama": {
      "BaseUrl": "http://localhost:11434",
      "ApiKey": "",
      "ChatModel": "qwen2.5:3b",
      "ExtractionModel": "qwen2.5:3b"
    },
    "Embedding": { "OllamaModel": "all-minilm" }
  }
}
```

### Option C — environment variables

Note the **double underscore** as the section separator. These win over both files.

```powershell
$env:RagLadder__Providers__Chat = 'ollama'
$env:RagLadder__Ollama__BaseUrl = 'http://localhost:11434'
```

```bash
export RagLadder__Providers__Chat='ollama'        # macOS / Linux
```

### The provider switches

Configuration is read, but providers still have to be selected:

```json
"Providers": {
  "Vector":   "memory",    // or "qdrant"
  "Graph":    "memory",    // or "neo4j"
  "Chat":     "ollama",    // or "openai", "replay"
  "Embedder": "onnx",      // or "ollama", "hash"
  "Reranker": "onnx",      // or "llm", "lexical"
  "FallbackToLocal": true
}
```

With `FallbackToLocal: true`, selecting a hosted provider with no credentials logs a warning and
falls back rather than failing startup. Set it to `false` if you would rather the app refuse to
start than quietly run degraded — a reasonable choice for demo day.

---

## 10. Verify

Start the app (§11) and check health:

```powershell
Invoke-RestMethod http://localhost:5099/api/health | ConvertTo-Json -Depth 4
```

Or open <http://localhost:5099> — the header shows a health pill and the Documents tab has the
full breakdown.

**A fully configured local setup reports `"status": "ok"` with all five providers `ok`:**

```
embedder  ok   all-minilm, 384 dims, served by Ollama.
reranker  ok   Chat model scoring (query and passage judged together).
vector    ok   Local SQLite store.
graph     ok   Local SQLite graph.
ollama    ok   local Ollama at http://localhost:11434/: 2 model tag(s) available.
```

**The embedder probe is the acceptance test from the spec.** It embeds three sentences and checks
that two paraphrases score above 0.7 cosine while an unrelated sentence scores below 0.3:

```json
"embedder": { "similarPair": 0.881, "unrelatedPair": -0.112, "passed": true }
```

If `passed` is false while a real model is loaded, the ONNX export is probably not the
sentence-transformers checkpoint — `pwsh tools/fetch-models.ps1 -Force`.

Every `degraded`, `paused`, `not-configured` or `unreachable` provider carries a sentence saying
exactly what is wrong. Read it; they are written to be actionable.

---

## 11. Run the app

### Day to day

```powershell
pwsh tools/demo.ps1 start        # containers if needed, then the app, then waits for health
pwsh tools/demo.ps1 stop
pwsh tools/demo.ps1 restart
pwsh tools/demo.ps1 status
```

`start` does the things that went wrong often enough to be worth automating: it brings the
containers up first (starting the app against a cold Qdrant is how you end up demoing on the SQLite
fallback without noticing), waits for `/api/health` to actually answer before saying ready, prints
what every provider resolved to, and warns if any of them fell back or the embedder probe is below
band. It also reports how many answers are warm, so you know whether the ladder will replay
instantly. If the app dies during startup it tails `data/app.err` rather than leaving you to find it.

`stop` matches this app by command line, so it will not take unrelated `dotnet` processes with it,
and it waits for the port to be released — a `restart` straight after would otherwise fail on an
address already in use. Containers are left up unless you pass `-Containers`, because restarting
Ollama evicts the model from memory and costs one slow answer afterwards.

Useful flags: `-Build` to rebuild first, `-Open` to launch the browser once healthy, `-Port 8080`
if 5099 is taken, `-TimeoutSeconds` if your machine is slow to start.

Output on a healthy start:

```
  containers:
    ragladder-neo4j    Up 21 hours (healthy)
    ragladder-ollama   Up 23 hours (healthy)
    ragladder-qdrant   Up 27 hours
Starting the app on port 5099…
  ready.
  health: ok
    embedder  ok   all-minilm, 384 dims, served by Ollama.
    reranker  ok   Chat model scoring.
    vector    ok   Qdrant reachable, 3 collections.
    graph     ok   Neo4j reachable, 227 nodes.
    ollama    ok   local Ollama at http://localhost:11434/: 7 model tag(s) available.
  answers cached: 12/50
    [12 rungs: 0,1,2,3,4,5,6,7,8,9,10,11] Who plays Peter Parker?
```

### By hand

```powershell
dotnet run --project src/RagLadder.Api
```

Serves <http://localhost:5099> and opens a browser. `Ctrl+C` to stop. If you are on the local
Ollama route, make sure the container is up first (`docker compose up -d`). Note that `dotnet run`
spawns a child process, which is why `demo.ps1` launches the built DLL directly — one process, one
PID, a clean stop.

The port comes from the `http` launch profile. Change `applicationUrl` in
`src/RagLadder.Api/Properties/launchSettings.json`, or override for one run:

```powershell
dotnet run --project src/RagLadder.Api --urls http://localhost:8080
```

### Replay — no network at all

Serves recorded model responses from `recordings/`.

```powershell
dotnet run --project src/RagLadder.Api --launch-profile replay
```

### Record — capture a pass for replay

```powershell
dotnet run --project src/RagLadder.Api --launch-profile record
```

### From a published build

```powershell
dotnet publish src/RagLadder.Api -c Release -o ./publish
dotnet ./publish/RagLadder.Api.dll
```

Configured paths (`models/`, `corpus/`, `config/`, `data/`) resolve against the repository root
regardless of where you launch from.

### Presentation mode

<http://localhost:5099/?present=1> — larger type, controls hidden, stage name / question / answer /
top chunks only.

---

## 12. The demo run-through

1. **Documents** → *Load committed demo corpus*.
2. **Process** → mode `thorough`, leave the review gate on → *Process*.
   Watch the eleven steps. It stops at step 9 and waits for you — that pause is deliberate.
   On a local CPU model this is the slow part; see the speed note in §7.2.
3. **Review** → read the funnel (*"341 proposed → 194 committed"* is the honest number), check the
   quality metrics, spot-check evidence spans with `↗ source`, then *Commit to graph*.
4. **Ask** → a chat view. Pick a stage from the row above the input, type, press Enter.
   Each exchange shows as a chat turn with a stage badge, so re-asking the same question at a
   different rung builds the comparison in one scrolling transcript.
   **Ask `Who plays Peter Parker?` at stage 0, then again at stage 3** — see §12.1.
   Tick **show the work** to expand retrieval, graph and the assembled prompt under each answer —
   collapsed by default so the chat reads as a chat, but never more than one click away.
5. **Compare** → the same question at two rungs, side by side.
   Try `How many features has Niraj Ranasinghe appeared in as Peter Parker?` at stages 1 and 2 —
   that is trap 1, and stage 1 can only ever see half the filmography.
6. **Graph** → drag the confidence slider and watch edges disappear. Click an edge for its evidence
   span and source chunk. Derived edges are dashed.
7. **Explore** → pick two people, hit *Connect*. This is the finale; vector search cannot answer
   it at any `k`.
8. **Eval** → *Load hand-authored set*, then *Run eval*. The per-type heatmap is the interesting
   artefact, not the overall curve.

On the Ask tab, number keys `0`–`9` jump between stages.

### 12.1 One question, every rung — the demo to actually give

Type this into the Ask chat and change only the stage:

> **Who plays Peter Parker?**

It is the best single question in the corpus because the corpus *disagrees with the real world on
purpose*. Section 4 says Chathura Pathirana (2002). Section 3 records both recasts. Section 14 says
Niraj Ranasinghe holds the role now. So the question has a right answer, a stale answer, and a
famous wrong answer — and each rung of the ladder picks a different one.

**Stage 0 — no retrieval. Measured output:**

> *"Peter Parker is played by **Tom Holland**… Prior to this, **Andrew Garfield** portrayed Peter
> Parker before being replaced by **Tobey Maguire**."*

Stop and let that land. Nothing in that sentence is in the corpus — the model answered from memory
about a document it was never shown. It is also **wrong about the real world**: Maguire came
before Garfield, not after. That is the honest picture of an ungrounded LLM: fluent, confident,
and unfalsifiable unless you already knew the answer.

**Stage 2 — chunking. Measured output:**

> *"Niraj Ranasinghe and Chathura Pathirana play Peter Parker in Spider-Man: No Way Home (2021)…
> Chathura Pathirana played the role from Spider-Man (2002) through Spider-Man 3 (2007)."*

Now every name comes from the document. Tom Holland is gone. The answer is grounded but
unprioritised — it recites the whole casting history because nothing has told it which record is
current.

**Stage 3 — metadata filter. Measured output:**

> *"Niraj Ranasinghe and Chathura Pathirana play Peter Parker in Spider-Man: No Way Home (2021)."*

Same facts, the history dropped. `year` on the chunk is now a filter, so the 2002 title record
stops competing with the current one. This is trap 2 — superseded casting — closing.

**Stages 4–11 — measured, and not a straight line.** Here is the whole ladder on this question,
taken from one sweep. Read it before you promise an audience that every rung is an improvement.

| Stage | Answer names | Time |
|---|---|---|
| 0 No RAG | Tom Holland, Garfield, Maguire — **none in the corpus** | 23 s |
| 1 Naive RAG | Niraj Ranasinghe | 89 s |
| 2 Chunking | Ranasinghe + Pathirana + full recast history | 42 s |
| 3 Metadata filter | Ranasinghe + Pathirana, history dropped | 7 s |
| 4 Hybrid search | as stage 3 | 36 s |
| 5 Reranking | as stage 3 | 219 s |
| 6 Query rewrite | as stage 3 | 266 s |
| 7 Contextual chunks | **Pathirana first**, Raimi continuity framing | 311 s |
| 8 Citations | Pathirana (2002), Ranasinghe (2016), Pathirana in NWH | 74 s |
| 9 Agentic | Pathirana, Raimi continuity | 401 s |
| 10 Graph | Pathirana across all three Raimi films **by name and year** | 283 s |
| 11 Router | as stage 8 | 38 s |

**The honest reading.** The big jump is 0 → 1: hallucination to grounding. Everything after that is
a change in *emphasis*, and from stage 7 the contextual prefixes push the model toward the Raimi
continuity, so Pathirana leads rather than Ranasinghe. That is not a regression in retrieval — it
is an ambiguous question meeting a corpus where three performers really did hold the role, and
each rung weights the evidence differently. Stage 10 gives the most specific answer of the twelve
because the graph supplies exact titles and years.

If you need a rung-by-rung improvement story instead, use the three questions in §12.2, which have
one right answer each and flip at a known rung. Use *this* question for the one thing it shows
better than anything else in the demo: the difference between a model talking about your document
and a model talking about its own memory.

Tick **show the work** under any answer to watch retrieval and the assembled prompt change
underneath a sentence that barely moves. That is the real lesson of the ladder: most rungs improve
*what the model was given*, not how it writes.

**Timing note.** Those are cold-cache seconds on a 4-core CPU with `qwen2.5:3b`. Stages 5–9 are
slow because they make several model calls per question — reranking judges each passage, query
rewrite adds a call, stage 9 iterates. **Warm them before you present**, with §12.3.

### 12.3 Warm the cache before you present

A cold rung costs between seven seconds and seven minutes here. Answer each one once, ahead of
time, and every one of them replays instantly:

```powershell
pwsh tools/warm-cache.ps1                                     # 'Who plays Peter Parker?', stages 0-11
pwsh tools/warm-cache.ps1 -Question 'Who did the music for Spider-Man: Homecoming?'
pwsh tools/warm-cache.ps1 -Stages 0,1,2,3                     # just the rungs you plan to show
pwsh tools/warm-cache.ps1 -Show                               # what is warm right now
pwsh tools/warm-cache.ps1 -Clear                              # start again
```

It prints each rung as it lands:

```
   0  live      23s  In the Marvel Cinematic Universe (MCU), Peter Parker is played by Tom Holland…
   1  live      89s  Niraj Ranasinghe as Peter Parker in Spider-Man: No Way Home (2021).
   2  live      42s  Niraj Ranasinghe and Chathura Pathirana play Peter Parker in Spider-Man: No Way…
```

**The cache holds the fifty most recently used answers and survives a restart**, so the warm-up is
a one-off rather than a morning ritual. It is keyed on the document, the question and every
resolved stage flag, so no two rungs can share an entry and a stale answer is impossible — change
the corpus, the graph or the model and the old entries simply stop being consulted.

On the Ask tab, a **green dot on a stage button** means that rung is already warm for whatever is
currently in the question box, and the line beside *show the work* says how many of the twelve are
ready. Check that before you start talking rather than after.

The same thing over HTTP, if you would rather script it:

```powershell
Invoke-RestMethod -Method Post http://localhost:5099/api/ask/warm -ContentType 'application/json' `
  -Body '{"documentId":"doc_xxx","question":"Who plays Peter Parker?"}'
Invoke-RestMethod http://localhost:5099/api/ask/cache            # what is held
Invoke-RestMethod -Method Delete http://localhost:5099/api/ask/cache
```

### 12.4 Show the work

Tick **show the work** under the composer and every answer carries its own reasoning trail. It
opens with **What stage _n_ did** — the pipeline as it actually ran, one row per step, with the
steps that were *skipped* shown as skipped rather than left out:

| Step | Ran | What happened | ms |
|---|---|---|---|
| Rewrite the query | skipped | the question is searched verbatim | — |
| Embed and search | yes | recursive collection, 50 candidates | 310 |
| Rerank | yes | 50 candidates rescored down to 5 | 219000 |
| Graph traversal | skipped | the graph is not consulted | — |

An absent step is the most informative thing about a low rung, which is why they are listed rather
than hidden. The panel also names what is new at this rung compared with the one below it, and
which traps the rung is meant to fix.

Below that, as before: the query rewrite, the router's decision, the agentic trace, the graph, all
retrieved chunks with scores and rank deltas, the full candidate list, and the exact prompt that
was sent.

**Graph stages draw the traversal.** Stage 10 and any custom flag set with graph expansion render
the neighbourhood the answer actually walked — not the whole graph. Path edges are drawn bold, the
seed entities are ringed, derived edges are dashed, and every node and edge carries a tooltip. It
is the same force-directed renderer the Graph tab uses, so the small picture and the full tab can
never disagree. The edge table and Cypher stay underneath it.

### 12.2 Three more that break at a known rung

| Question | Rung where it flips | What it proves |
|---|---|---|
| `How many features has Niraj Ranasinghe appeared in as Peter Parker?` | 1 → 2 | Stage 1 chunks by page and can only see credits 1–3, so it answers 3. Stage 2 chunks by section and retrieves all six. |
| `Who did the music for Spider-Man: Homecoming?` | 5 → 6 | The corpus says "original score composed by". Lexical search misses the colloquial "music" until query expansion is on. |
| `How is Isuru Obeysekera connected to Nethmi Tomei?` | 9 → 10 | Four hops through *No Way Home*, and the two never appear in a shared chunk. Vector search cannot answer this at any `k`; the graph answers it directly. |

**A caveat on counting questions, stated plainly.** On `qwen2.5:3b` the filmography question
answers **5** at stage 2 when the corpus says six. Check *show the work*: retrieval is correct —
Section 14 comes back whole, with all six credits — and the model then miscounts them. That is a
generation failure at 3B, not a retrieval failure, and the rung transition it is meant to
demonstrate (3 → "more than 3") still fires. Prefer the name-flip questions above for a live
audience, where a small model is on much safer ground.

---

## 13. Testing

### Unit and integration tests — no network, no keys

```powershell
dotnet test
```

**86 tests** (one skipped without Neo4j credentials). The integration suite boots the real application against the local providers and a
scripted model, then walks the whole pipeline: load, process, review gate, commit, traverse,
aggregate, ask across the ladder. It also pins the traps, so a change that quietly stops trap 1
from firing fails the build.

```powershell
dotnet test --filter "FullyQualifiedName~Trap_one"
```

### Testing against your real Neo4j

One test is skipped by default because it needs a live instance. It commits a miniature graph,
computes derived edges, runs `shortestPath`, runs two aggregations, checks the type barrier held,
then deletes everything it wrote — safe to point at the instance you demo from.

```powershell
$env:RAGLADDER_TEST_NEO4J_URI      = 'neo4j+s://<instance-id>.databases.neo4j.io'
$env:RAGLADDER_TEST_NEO4J_PASSWORD = '<password>'
dotnet test --filter Neo4j
```

Run this once after setting up Aura. It exercises the Cypher — dynamic labels, `UNWIND` batches,
constraints, traversal — in about 30 seconds, which is far faster than discovering a problem
through a full corpus pass.

### End-to-end smoke test — against a running instance

```powershell
# terminal 1
dotnet run --project src/RagLadder.Api

# terminal 2
pwsh tools/smoke-test.ps1
```

Walks the demo the way a person would and prints the funnel, the ladder timings and the eval
heatmap. Checks needing a live model **skip rather than fail**, and say which. Exit code is
non-zero only on real failures, so it works in a pre-demo check.

```powershell
pwsh tools/smoke-test.ps1 -BaseUrl http://localhost:8080 -ChunkCap 20
```

Use `-ChunkCap 20` on a local CPU model, or the extraction step will take a very long time.

### What good looks like

- `dotnet test` — 79 passed, 1 skipped.
- `smoke-test.ps1` with **no chat model** — 21 passed, 0 failed, 11 skipped.
- `smoke-test.ps1` with everything configured — the skips become passes as each capability
  becomes available.

---

## 14. Demo-day preparation

Do this the **day before**, not the morning of.

1. **Wake Neo4j.** Open <https://console.neo4j.io> and confirm the instance is running. The free
   tier pauses when idle; this is the most likely failure and the slowest to fix under pressure.
   If you use Qdrant Cloud rather than the container, check that too.
2. **Start the containers:** `docker compose up -d`. Confirm all three endpoints answer:

   ```powershell
   Invoke-RestMethod http://localhost:11434/api/version    # ollama
   Invoke-RestMethod http://localhost:6333/                # qdrant
   Test-NetConnection <instance>.databases.neo4j.io -Port 7687   # neo4j bolt
   ```
3. **Do a full pass and warm the caches.** Process the corpus end to end and commit the graph.
   On a CPU model this is 30–60 minutes once, and free thereafter:

   ```powershell
   pwsh tools/smoke-test.ps1
   ```

4. **Record a replay pass.** Run with `--launch-profile record`, work through the golden set at
   every stage, then confirm `--launch-profile replay` reproduces it with the network off. This is
   the hour that saves the session.
5. **Consider committing the warmed state.** `data/ragladder.db` holds the caches and the local
   stores. It is gitignored by default; commit it deliberately if a colleague should be able to
   clone and demo instantly.
6. **Run the smoke test once more** and read the warnings, not just the pass count.
7. **Check the health pill is green** in the browser you will actually present from.

---

## 15. Routine operations

### Resetting state

All durable state is one SQLite file plus the uploads folder:

```powershell
# stop the app first
Remove-Item data/ragladder.db* -Force
Remove-Item data/uploads -Recurse -Force
```

Next start recreates the schema. This also clears the caches, so the next process run pays full
model cost again.

To remove one document instead — including its vectors and graph nodes — use **Delete** on the
Documents tab, or `DELETE /api/documents/{id}`.

### Managing the containers

```powershell
docker compose up -d                                   # start ollama + qdrant
docker compose down                                    # stop, data kept
docker compose down -v                                 # stop and delete both volumes
docker compose ps                                      # what is running

docker exec ragladder-ollama ollama list               # installed models
docker exec ragladder-ollama ollama pull qwen2.5:7b    # add a model
docker logs ragladder-ollama --tail 50                 # diagnose

start http://localhost:6333/dashboard                  # qdrant collections and points
docker logs ragladder-qdrant --tail 50
```

Data lives in named volumes (`ragladder-ollama`, `ragladder-qdrant`), so `docker compose down`
keeps both the models and the indexed vectors.

### Clearing a store

Deleting a document through the app removes its Qdrant collections and Neo4j nodes. To wipe
everything:

```powershell
# Qdrant — drop one collection
Invoke-RestMethod -Method Delete http://localhost:6333/collections/<docId>_recursive

# Neo4j — from the Aura console or the Browser
# MATCH (n) DETACH DELETE n
```

### Controlling cost and time

Extraction dominates: roughly one model call per chunk, plus a batched verification pass in
thorough mode, plus one summary call per section. Levers, in the order to reach for them:

| Lever | Where | Effect |
|---|---|---|
| Warm caches | automatic | Reprocessing an unchanged document is free |
| Skip LLM section summaries | Process tab | Removes 92 calls — the largest single cost on CPU |
| `quick` mode | Process tab | Skips verification, roughly 1 call per chunk |
| Chunk cap | Process tab, default 120 | Caps extraction; always warns how many chunks were left out |
| Vectors only | Process tab | No extraction calls at all |
| GPU | `docker-compose.yml` | Minutes become seconds |
| Replay mode | `--launch-profile replay` | Zero calls |

Rate limiting is handled: three retries at 1s/2s/4s on 429 and 503, at most two concurrent calls.

### Changing the ontology

`config/film-ontology.json` defines what the extractor may produce. Edit it, then bump
`RagLadder:Extraction:OntologyVersion` — the version is part of the extraction cache key, so
changing it forces re-extraction. Leaving it unchanged after an edit means you keep serving cached
results from the old ontology.

A missing or malformed file falls back to an identical built-in ontology rather than failing.

### Where things live

| Path | Contents | Gitignored |
|---|---|---|
| `data/ragladder.db` | Documents, chunks, caches, review state, local vector and graph stores | yes |
| `data/uploads/` | Uploaded PDFs | yes |
| `models/` | ONNX models | yes |
| `recordings/` | Recorded model responses | no — commit these |
| `corpus/demo/` | Generated PDF, golden set, page-break anchors | PDF is committed |
| `config/` | Ontology, diminutives table | no |
| Docker volume `ragladder-ollama` | Ollama models | n/a |

---

## 16. Running with no chat model

Useful for development, for CI, and for a first look before anyone has signed up for anything.

```powershell
dotnet build
dotnet run --project tools/RagLadder.CorpusBuilder
dotnet run --project src/RagLadder.Api
```

**Works:** PDF parsing, sectioning and front matter, all three chunking strategies, vector and
hybrid retrieval, reranking, metadata filtering, the graph store, and every trap that lives in
chunking or retrieval.

**Does not:** anything needing a model call — generated answers, LLM extraction (so no knowledge
graph, no stage-10 traversal or aggregation), section summaries fall back to a deterministic
heading-based form, and query rewrite passes the question through unchanged.

Health reports `degraded` and names every substitution. Nothing is silent.

**This is a development mode, not a demo mode.** Before presenting, get a chat model (§7) and a
real embedder (§6) — those two turn the ladder from a plumbing diagram into an argument.

---

## 17. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Health says embedder `degraded` | No model files | `pwsh tools/fetch-models.ps1`; if blocked, §6.2–6.4 |
| Embedder probe `passed: false` with a real model | Wrong ONNX export | `pwsh tools/fetch-models.ps1 -Force` |
| Every answer is `Not found in the provided documents.` | No chat provider | Pick a route in §7; health should read chat `ok` |
| Chat `not-configured` but Ollama is local | Empty API key on a non-local URL | Set `Ollama:BaseUrl` to `http://localhost:11434`; a local instance needs no key |
| Chat `degraded`, "tag not listed" | Model not pulled, or cloud tag renamed | Local: `docker exec ragladder-ollama ollama pull <tag>`. Cloud: list tags (§7.1) |
| A `-cloud` tag pulls but inference returns 502 | TLS to ollama.com intercepted, or no API key | §7.3 — mount your corporate CA and set `OLLAMA_API_KEY`, or use a local model |
| Neo4j `unreachable` but the console shows it running | Outbound TCP 7687 blocked | `Test-NetConnection <host> -Port 7687`. If blocked, use the local graph store |
| Qdrant `unreachable` on localhost | Container not started | `docker compose up -d`; check `docker logs ragladder-qdrant` |
| Stage 4 finds nothing by keyword | Collection created without the full-text index | Check `GET /collections/<name>` shows `text: text` in `payload_schema`; delete the collection and reprocess |
| `setup-ollama.ps1`: registry unreachable | `registry.ollama.ai` blocked too | §7.3, or import a GGUF offline (§7.2) |
| `setup-ollama.ps1`: daemon not running | Docker Desktop not started | Start it and re-run |
| Processing sits on "Summarising sections" | One call per section, ~92 of them; the chunk cap does not apply | Expected on a CPU model — 30–60 minutes once, cached after. Tick *vectors only* if you just want retrieval |
| Extraction very slow | A 3B+ model on CPU | Lower the chunk cap; enable the GPU block in `docker-compose.yml` |
| Extraction slow *and* `MaxConcurrency` is 2 | Two request streams evicting each other's cached prompt prefix | Set `Ollama:MaxConcurrency` and `OLLAMA_NUM_PARALLEL` to 1. Serialising is roughly ten times faster against a local model — see the concurrency note in §7.2 |
| Extraction "runs" but the funnel stays at zero | Calls exceeding `Ollama:TimeoutSeconds` are retried three times and then the chunk is skipped | The default is 600 s for this reason. On CPU a 7B extraction call can take 10 minutes — raise it further, or use a smaller model |
| Funnel commits single-digit edges, `relation-endpoint-missing` dominates | The local model proposes relations without declaring their endpoints as entities — a 3B capability gap, not a tuning problem | §17.1 — export the prompts, run them through a capable model, import. Measured: 8 edges locally, 210 imported |
| Asking a question hangs while processing runs | Interactive chat queued behind bulk extraction calls | Fixed — chat and extraction hold separate concurrency gates. If you see it on an older build, wait for extraction or restart |
| No stage buttons, and Send does nothing | The page booted while the API was down — usually a tab left open across a restart | It now shows a red banner with a Retry button and retries once by itself. Reload if you are on an older build |
| Changed the model but nothing changed | The app reads configuration at startup | Restart it. `GET /api/config` shows the models actually in use |
| Two models loaded at once, everything crawls | A previous model is still resident | `docker exec ragladder-ollama ollama ps`, then `ollama stop <tag>`. Two models on a 4-core VM contend badly |
| Ollama returns 500 on a chat call | Memory pressure — often another model resident, or a large pull running | `ollama ps` and stop what you are not using; retry once the machine is quiet |
| A model never answers, even a one-word prompt | It does not fit the container's memory limit and is thrashing | `docker stats --no-stream` shows the ceiling. Use a model under half of it, or raise `memory=` in `%USERPROFILE%\.wslconfig` and `wsl --shutdown` |
| Memory stays high with nothing in `ollama ps` | A runner process is stuck after a failed load | `docker restart ragladder-ollama`. Measured here: 10.79 GiB held with no model loaded, back to 79 MiB after a restart |
| `ollama pull` stops partway and never resumes | The pull dies with the shell that started it | Run it detached: `docker exec -d ragladder-ollama ollama pull <tag>`, or leave the foreground command running. Partial blobs are kept, so re-running resumes |
| Ollama answers time out | Model reloading per call | `OLLAMA_KEEP_ALIVE` is 30m in `docker-compose.yml`; check the container picked it up |
| Health says vector or graph `paused` | Free tier suspended when idle | Resume in the Qdrant or Neo4j console; the detail line says which |
| Restore fails with a 401 | Private NuGet feed configured globally | `NuGet.config` pins to nuget.org; check `dotnet nuget list source` |
| Port 5099 already in use | Something else has it | `dotnet run --project src/RagLadder.Api --urls http://localhost:8080` |
| Upload rejected, 422 | Scanned PDF, no text layer | Out of scope by design. Use a text-layer PDF |
| Stage 1 and stage 2 answer identically | `forced breaks: 0` when building the corpus | `corpus/demo/pagebreaks.json` missing; rebuild (§5) |
| Stage 10 finds no entities | Graph never committed | Process, then **Commit to graph** on the Review tab |
| Graph tab empty | Same, or the confidence slider is too high | Commit first; drag the slider to 0 |
| Path query finds nothing | No credit chain, or hops too low | Raise *max hops*; try *Random pair* |
| Eval all zeros except `ungrounded` | No chat provider — every answer is a refusal, which correctly passes only the control group | Configure a chat model (§7) |
| `SQLite Error 5: database is locked` | Two instances sharing `data/` | Run one at a time, or point the second at another `DataDirectory` |
| Processing fails at Parse | Encrypted or malformed PDF | Check the job warnings on the Process tab |

### Reading the logs

The startup banner prints the provider posture once, which is usually all you need:

```
RAG Ladder starting — status ok.
  embedder  ok   all-minilm, 384 dims, served by Ollama.
  ollama    ok   local Ollama at http://localhost:11434/: 2 model tag(s) available.
```

For more, set `Logging:LogLevel:Default` to `Debug` in `appsettings.json`.

---

## 17.1 Bring your own model — extraction elsewhere

**Use this when extraction quality is the problem, not extraction speed.**

Building the graph is the one step where a small local model can fail outright rather than merely
slowly. `qwen2.5:3b` inverts predicates, paraphrases evidence instead of quoting it, and — most
damaging — proposes relations whose endpoints it never declared as entities. On the 26-chunk seed
that produced a graph of **8 semantic edges**, because 54 relations were dropped for a missing
endpoint. No amount of filter tuning fixes that; it is a capability gap.

So externalise the model call. Export the exact prompts, run them through a capable model in any
chat window, and import the reply.

```powershell
# 1. write one self-contained document — 36,036 characters for the seed corpus
pwsh tools/import-extraction.ps1 -Export

# 2. paste extraction-request.md into a chat with a capable model, save the JSON reply

# 3. import it and reprocess
pwsh tools/import-extraction.ps1 -File response.json -Process
```

Step 3 seeds the extraction cache, so every chunk is a cache hit and **no model calls are made** —
reprocessing takes about a minute instead of the better part of an hour.

**What this does not bypass.** Only the model call moves. The reply lands in the same cache the
local model writes to, and from there the pipeline is byte-for-byte the one it always was: all
seven filters, evidence grounding, ontology conformance, direction checking, entity resolution,
the funnel, the review gate. An imported triple whose evidence is not a literal span of its chunk
is dropped exactly like a local one, and the funnel counts it. The demo's central claim — the
model proposes, deterministic code disposes — is untouched, which is the whole reason this is a
cache seed rather than a graph import.

Measured on the seed corpus, the same 26 chunks through a capable model:

```
FUNNEL  extracted 267 -> grounded 267 -> conformant 267 (0 flipped) -> committed 235
        126 entities: Person 56, Character 45, Film 13, Location 7, Studio 3, Franchise 2
```

Against **8 committed edges** from the local 3B model on the same chunks. Note `grounded 267` of
267 — a capable model quotes its evidence verbatim, so the filter that discards most of a small
model's output passes everything here. The filter did not get weaker; the input got better.

**Notes.**

- 36 KB fits one message comfortably. If a model refuses the length, split it — the chunk sections
  are independent, and several replies can be concatenated into one file before importing.
- Chunk ids must match exactly. Re-processing the document regenerates them, so re-export if you
  reprocess between the export and the import.
- The import reports `unknownChunkIds` and warns when chunks are missing; anything not imported
  falls back to the local model rather than silently vanishing.
- Two filter relaxations were added while chasing the local model's output: predicate
  normalisation (stripping the `-ACTED_IN->` arrows it copied out of the prompt) and evidence
  repair (recovering a span when every required name is present but the quote was paraphrased).
  Both are documented in `ExtractionFilters.cs` with the measurements that motivated them. With a
  capable model doing extraction neither is load-bearing any more.

---

## 18. Verification status

What has actually been exercised, so you know where to be careful.

**Verified on a network where huggingface.co and ollama.com are both blocked:**

- Build, all 86 tests, corpus generation, PDF parsing (92 sections, 89 with front matter), all
  three collections, the ladder across stages 0–11, traps 1 / 6 / 11 firing, cache isolation
  between stages, the golden set, the eval heatmap.
- **The embedding model via the Qdrant FastEmbed mirror**, clearing the acceptance band at
  0.881 / −0.112.
- **A chat model via Ollama in Docker**, models pulled from `registry.ollama.ai` while
  `ollama.com` was unreachable. `all-minilm` through the same container scores 0.79 / −0.04.
  Health reads `ok` on all five providers with no API key and no Hugging Face access.
- **The OpenAI-compatible client** against a local endpoint speaking that wire format: full
  pipeline including LLM extraction, the filter chain, graph commit with 200 Person nodes, path
  traversal, aggregation and generated answers.

**Verified end to end on the Spider-Man seed corpus:**

- **The bring-your-own-model round trip** — 26 chunks exported as one 36 KB document, run through
  a capable model, imported as cache seeds and reprocessed with zero local model calls. The graph
  that resulted: **126 entities** (56 Person, 45 Character, 13 Film, 7 Location, 3 Studio,
  2 Franchise), **235 semantic edges** and 292 derived `COLLABORATED_WITH`. Every one of the
  corpus's thirteen title records has its own node with its own cast and director.
- **The full ladder on one question**, stages 0–11, cold cache — transcript and timings in §12.1.
- **The stage-10 path query** — `Isuru Obeysekera` to `Nethmi Tomei` in 4 hops, with narrative:
  *"composed for Spider-Man (2002), which starred Rashmi Samaraweera, who acted in Spider-Man: Far
  From Home (2019), which starred Nethmi Tomei."* Neither shares a chunk with the other.
- **Trap 1 still fires on the seed.** Section 14 splits Niraj Ranasinghe's six credits 3/3 after
  the 2018 entry. Stage 1 answers "3"; stage 2 retrieves Section 14 whole and answers with more.
  See the caveat in §12.2 about `qwen2.5:3b` miscounting the six.
- **Trap 2 across the ladder**, question `Who plays Peter Parker?`: stage 0 answers "Tom Holland"
  from parametric memory with nothing from the corpus, stage 2 answers with corpus names and the
  full casting history, stage 3 drops the superseded history. Transcript in §12.1.
- **Neo4j in Docker** — the imported graph round-trips and is visible in Neo4j Browser at
  <http://localhost:7474>.

**Verified against the real hosted and containerised stores:**

- **Qdrant in Docker (1.18.2)** — all three collections created by the app with the correct shape:
  92 points, 384 dimensions, Cosine distance, and the **mandatory full-text index on `text`** that
  stage 4 depends on, alongside keyword and integer indexes for every metadata filter field.
- **Neo4j AuraDB** — the full graph implementation round-trips against a real instance
  (`dotnet test --filter Neo4j`): schema constraints, commit with dynamic labels and `UNWIND`
  batches, `COLLABORATED_WITH` derivation, `shortestPath` with its narrative, two aggregations,
  edge lookup with evidence, the Person/Character/TVSeries type barrier, and document deletion.
- **All five providers `ok` simultaneously**: Ollama in Docker for chat and embeddings, Qdrant in
  Docker for vectors, Neo4j Aura for the graph.

**Not verified:**

- **Ollama Cloud `-cloud` tags** — the model registers but inference returns 502 behind an
  SSL-inspecting proxy, and no Ollama API key was available. See §7.3.
- **An internal OpenAI-compatible gateway** — the client is exercised against a local endpoint
  speaking that wire format, but not against a real corporate gateway.
- **Qdrant Cloud** — the container was used instead. Same client, different host and an API key.
- **A complete full-corpus pass on the local CPU model** — measured at ~20 s per call. The
  pipeline, the stores and the traversal are proven; throughput is the constraint.

Treat your first green `/api/health` with everything configured as the real acceptance test.
