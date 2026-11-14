---
title: "Real downloads for local models (Ollama pull + HF Whisper GGML)"
version: "1.6"
date: 2026-05-16
updated: 2026-11-12
changes: "Third amendment of 2026-11-12: an installed Ollama model whose capabilities lack tools is marked No tools and the model in use says it cannot call tools, without blocking the choice (the owner's design); by default unknown capabilities say nothing and an empty list counts as known.."
page_type: adr
status: accepted
description: "The first iteration of \"Manage Downloaded Models\" used mocked state: clicking Download just flipped a flag in localStorage."
source:
  - "owner-notes/06-decisions/2026-05-16-local-models-real-downloads.md"
reliability: medium
---

# Real downloads for local models (Ollama pull + HF Whisper GGML)

## Context

The first iteration of "Manage Downloaded Models" used mocked state:
clicking Download just flipped a flag in localStorage. The user
quickly noticed `ollama list` was empty despite the UI claiming a
download had happened. To avoid lying to users — and to make the
screen actually useful — the Download button needs to perform a real
network operation against a real local backend.

Two backends are wired in this ADR:

- **Ollama** (`/api/pull`) for Local Analysis models — text LLMs in
  the Qwen 2.5 and Gemma 3 families.
- **HuggingFace** (`ggerganov/whisper.cpp`) for Local Transcription
  models — Whisper Large v3 and v3-Turbo in GGML format.

Each path streams progress events back to the renderer in real time
and persists the downloaded set so `ngOnInit` can resurrect state on
app reopen.

## Decisions

### Ollama integration

- New IPC channels:
  - `scout:llm.pullOllamaModel` (request/response handler)
  - `scout:llm.pullOllamaProgress` (one-way event from main →
    renderer, dispatched per chunk of the NDJSON stream)
  - `scout:llm.deleteOllamaModel` (request/response handler)
- Main process opens `POST <endpoint>/api/pull` with
  `{ name: <tag>, stream: true }` and reads the response body as a
  WHATWG `ReadableStream<Uint8Array>` directly. Each newline-
  delimited JSON line is parsed; `completed` and `total` fields drive
  the `percent` calculation. The pull is considered done when the
  stream emits `{ "status": "success" }` or terminates cleanly.
  Tradeoff: hand-rolled NDJSON parser instead of pulling in a
  library; the format is simple enough that this stays simple.
- Delete uses `DELETE <endpoint>/api/delete` with
  `{ name: <tag> }`. A 404 is treated as success (idempotent removal).
- **Ollama identity bootstrapping**: before each pull, the main
  process checks for `~/.ollama/id_ed25519`. If missing, it runs
  `ssh-keygen -t ed25519 -f <path> -N "" -q` as a subprocess to
  generate the key Ollama requires. Tradeoff: a hard dependency on
  `ssh-keygen` being on PATH (default on Linux/macOS/Windows 10+),
  in exchange for the user not needing to drop to a terminal on
  first run. If `ssh-keygen` is missing we surface a clear
  actionable error pointing them at `openssh-client`.
- **Catalog → Ollama tag mapping** lives in
  `local-models.component.ts` as a frozen `Record<string, string>`:

| Catalog id                  | Ollama tag      | Size  |
|-----------------------------|-----------------|-------|
| `qwen-2.5-0.5b-instruct`    | `qwen2.5:0.5b`  | 397 MB |
| `qwen-2.5-1.5b-instruct`    | `qwen2.5:1.5b`  | 986 MB |
| `qwen-3-vl-2b`              | `qwen2.5:3b`    | 1.9 GB |
| `gemma-3-4b`                | `gemma3:4b`     | 3.3 GB |
| `gemma-3-12b`               | `gemma3:12b`    | 8.1 GB |

  The `qwen-3-vl-2b` catalog id is a legacy identifier — the visible
  card is labelled "Qwen 2.5 3B" with a text-only badge. Renaming
  the id requires a localStorage migration and was deferred.

### Whisper integration

- New IPC channels:
  - `scout:whisper.listInstalled` — scans
    `<userData>/whisper-models/` for known GGML filenames and
    returns the set of variants present on disk.
  - `scout:whisper.downloadModel` (request/response)
  - `scout:whisper.downloadProgress` (one-way event)
  - `scout:whisper.deleteModel` (request/response)
- Models are pulled from the **ggerganov/whisper.cpp** Hugging Face
  repo, not from OpenAI's original PyTorch checkpoints. The GGML
  format pairs natively with whisper.cpp, which is what the future
  Whisper.net binding on the .NET worker will consume.
  Tradeoff: we depend on a third-party mirror (ggerganov is the
  upstream whisper.cpp maintainer, so the risk is low) in exchange
  for not shipping a PyTorch runtime in a desktop app.
- **Variant → file mapping**:

| Variant            | HF file                     | Size  |
|--------------------|-----------------------------|-------|
| `large-v3`         | `ggml-large-v3.bin`         | 2.9 GB |
| `large-v3-turbo`   | `ggml-large-v3-turbo.bin`   | 1.5 GB |

- **Atomic write**: download streams to `<file>.bin.partial`, then
  `rename()` to `<file>.bin` on success. Interrupted downloads leave
  a `.partial` file that the next attempt deletes before re-opening.
- **Progress source**: `Content-Length` header from HF + bytes
  received from the reader. Percent caps at 99 until the final
  rename, then 100 is emitted to unfreeze the UI. Tradeoff: a
  one-percent lie at the end in exchange for guarantee that 100%
  only fires when the file is renamed and verified.
- **Storage location**:
  `app.getPath('userData')/whisper-models/<file>.bin`. Cross-platform
  by Electron's standard path resolution (Linux:
  `~/.config/Scout Desktop/whisper-models/`, macOS:
  `~/Library/Application Support/Scout Desktop/whisper-models/`,
  Windows: `%APPDATA%\Scout Desktop\whisper-models\`).

### Error surfacing

- The renderer captures the rejection from
  `llmPullOllamaModel` / `whisperDownloadModel` and writes the message
  to:
  1. A per-row inline error component (`row-error`) under the model
     row, in red, with a "See logs" link to `/logs`.
  2. `LogStore.appendDiagnostic(...)`, a new method that fabricates
     a synthetic `WorkerEvent` (`kind: 'stderr'`) so the message
     appears in the Logs view alongside worker output.
- Error messages preserve Ollama's verbatim text (e.g.
  `pull model manifest: open id_ed25519: ...`) plus a prefix
  identifying the context (`[Ollama pull qwen2.5:3b]`).
  Tradeoff: log lines are not strongly typed against an error code,
  but the messages are searchable and link directly to the failing
  row in the UI.

### State synchronisation

- On `LocalModelsComponent.ngOnInit`, the renderer calls
  `whisperListInstalled()` and reconciles its response against
  `LocalModelsStore`: any variant present on disk that the store
  does not flag as downloaded is marked downloaded. The Ollama side
  does **not** have an equivalent reconciliation today — the store
  is the single source of truth for what the Analysis tab shows.
  Tradeoff: a model pulled outside the app (via `ollama pull` in a
  terminal) will not appear in the Models tab. Accepted because the
  product flow funnels every pull through the in-app screen.
- Read-only invariant: all writes touch only the user's own machine
  (`/api/delete` against the local Ollama daemon, `fs.rm` on
  whisper model files, ed25519 key generation in the user's home
  directory). Added to `LOCAL_WRITE_ALLOWLIST` in
  `shared/test/ipc-contract.test.ts` with justification.

## Deferrals

- Whisper inference on the .NET worker (Whisper.net + GGML).
  Download is wired, runtime is not.
- Cancellation of an in-flight pull. Today the user can leave the
  screen but the download continues in the background until success
  or failure.
- Concurrent download throttling. Multiple downloads happen in
  parallel without rate limiting.
- Auto-resume of `.partial` files after a crash. Today the next
  attempt deletes the partial and re-downloads from scratch.
- Persistent download history beyond the current `downloaded` set.

## Open questions

- Should we verify the SHA of downloaded files against HF's manifest
  to detect mid-stream corruption? Cheap to add but no concrete
  motivating incident yet.
- Should `qwen-3-vl-2b` be renamed in the catalog now that it maps
  to a text-only Ollama tag? Touches a localStorage shape and was
  deferred to avoid breaking existing user state.

## Amendment 2026-11-08: no Ollama key from Scout, a pull ends on success, Cancel and a stall window

The owner decided on 2026-11-07 that Scout writes only in its own folders, and the Ollama identity bootstrapping above wrote `~/.ollama/id_ed25519` in the owner's home with `ssh-keygen` before the first pull, silently. It is superseded: Scout no longer checks for the key, runs `ssh-keygen` or creates `~/.ollama`; the Ollama panel in AI Sources tells the owner to "Start Ollama once before the first download: Ollama creates its own key the first time it runs.", which is what Ollama does when its server first starts. The text above is kept as the record of the first iteration.

Two more bullets above no longer hold. A pull is done only when the stream emits `{ "status": "success" }`: a stream that ends without it, or with an error line, fails with the reason, so a killed Ollama is never reported as a finished download. The deferred cancellation now exists: Cancel on a row that is downloading stops a Whisper download or an Ollama pull, and every window shows it as cancelled rather than as an error; a Whisper download that receives nothing for 60 s, and an Ollama pull that does while it reports byte progress, stop with the reason, while a phase Ollama reports only by status (pulling the manifest, verifying the digest, writing the manifest) has a window of its own, 10 minutes, and stops with "Ollama reported no progress for 10 minutes; the download stopped.". [[pages/services/desktop-ui]] (Local Models screen) and [[pages/services/desktop-channels]] (Whisper models) carry the current behaviour; [[pages/runbooks/local-models]] the operator's side.

## Amendment 2026-11-11: the embedding model comes with the chat model, on the same click

With Ollama as the provider the worker builds its skill index with embeddings from the Ollama model `nomic-embed-text` (its default in `src/Scout.Llm/Configuration/LlmOptions.cs:282`), but the desktop pulled only the chat model the owner picked, so on every install made through the desktop the index build failed with HTTP 404 and skills were picked by the words of the message alone. The design chosen on 2026-11-11: a Download on an Ollama chat model in "Local models in Ollama" also pulls `nomic-embed-text`, and the dialog says so with its size; a fixed "Skill matching" row shows Installed, or Download with its progress, Cancel and its failure, which covers an install that already has a chat model; it has no Remove. Both pulls are the owner's click over the existing `scout:llm.pullOllamaModel` channel, each with its own request id, so one can fail or be cancelled without the other; nothing is pulled at worker start or in the background. The renderer reads Ollama's list (`scout:llm.listOllamaModels`) when the dialog opens and counts the model installed only when it lists `nomic-embed-text` or `nomic-embed-text:latest`, which is what the worker's request resolves to; another tag alone, such as `nomic-embed-text:v1.5`, reads as not installed. The name is one shared constant (`OLLAMA_EMBEDDING_MODEL`, `ui/packages/shared/src/ipc-contract.ts:434`) that the desktop also passes to the worker as `SCOUT__LLM__OLLAMA__EMBEDDINGS__MODEL` (`ui/packages/main/src/worker/build-env.ts:155`), the key `Scout:Llm:Ollama:Embeddings:Model` binds (`src/Scout.Llm/LlmServiceCollectionExtensions.cs:59`, `src/Scout.Llm/Configuration/LlmOptions.cs:266`), so what is downloaded and what is asked for cannot drift. The size the dialog states, 274 MB, is the Ollama library's published figure; the model was not installed on the machine that made the change. [[pages/services/desktop-ui]] (Ollama models on the Ollama card) carries the current behaviour.

## Amendment 2026-11-11: the hint to start Ollama before the first download is gone

The amendment of 2026-11-08 had the Ollama panel say "Start Ollama once before the first download: Ollama creates its own key the first time it runs.". That hint left the app on 2026-11-11 together with the panel's Autostart, Start, Stop and Restart, which the owner deferred: Scout does not start or stop Ollama, so the hint pointed at a Start that never worked, and it showed even on an Ollama that had answered for months, which has created its key already. The panel now says "Ollama is run by this computer, not by Scout.", or, while Ollama does not answer its model list, "Ollama is not answering. Open the Ollama app or run ollama serve, then scan again."; an Ollama that has started once has its key. The rest of the 2026-11-08 amendment holds: Scout creates no key and runs no `ssh-keygen`. [[pages/services/desktop-ui]] carries the panel as it is now; [[pages/roadmap/ollama-local-models]] the decision.

## Amendment 2026-11-12: every installed Ollama model can be chosen

The owner, 2026-11-12, translated: "the way it is structured today, regarding Ollama, it should list all the models and let me choose whichever I want". Until then the dialog "Local models in Ollama" and Models, Analysis showed only the five catalog models, so a model installed with the Ollama CLI was invisible and could not be chosen. The design the owner approved, in three points:

- **The installed list comes from the server.** Installed shows every model `GET /api/tags` lists, in its order, except the skill-matching model, which keeps its own row. A catalog model keeps its catalog copy; any other is titled by its tag and says its family, parameter size, quantization and size on disk, each when Ollama reports it.
- **Any installed model can be chosen.** Use this saves the tag itself as `llm.model`, on the owner's click only; reading the list, on open or on the 4 s poll, never chooses a model and writes nothing. The worker gets the same string as `SCOUT__LLM__OLLAMA__CHAT__MODEL`, unchanged. Stored selections need no migration: the catalog ids stay valid keys and any other key is a tag; the legacy id `qwen-3-vl-2b` stays.
- **The catalog models stay as Recommended.** The ones not installed are listed under "Recommended" (formerly "Pull a model from the Ollama library") with Download.

Two defaults in force unless the owner says otherwise ([[pages/roadmap/ollama-local-models]], "Defaults in force"):

- **Embeddings-only models are listed but never chosen.** Main reads each model's capabilities with one `POST /api/show` (2 s, kept by digest once read; a failure leaves them unknown and never fails the list). A model whose capabilities list `embedding` without `completion`, or, when unknown, whose name says embed or whose family is a BERT, shows "Embeddings only" with Remove and no Use this, and is never a row in Models.
- **The model in use cannot be removed.** While Ollama is the provider in use, or none is saved, Remove on the model in use is disabled with "In use. Choose another model before removing it."; before, Remove deleted the model and silently moved the selection to another one.

Pull by name, a link to the Ollama library and a warning for a model that cannot call tools are separate steps, tracked in [[pages/roadmap/ollama-local-models]]. No new channel and no new kind of write: `/api/show` is a read of the local server, and Use this writes `llm.model` in Scout's own vault as before ([[pages/invariants/no-executive-writes]]).

## Second amendment 2026-11-12: Pull by name and Browse the Ollama library

The owner, 2026-11-12, translated: "it should list all the models and let me choose whichever I want... I do not know if the way it is structured this works https://ollama.com/search". The library has thousands of models and no catalog Scout could ship, so the design the owner approved is the installed list of the amendment above, then a field that pulls any model of the library by its name and a link that opens the library in his own browser. Scout reads no catalog from the library and scrapes nothing; the only request to that site is the owner's browser opening the page.

What the owner approved:

- **Pull by name.** "Pull by name" sits between Installed and Recommended in "Local models in Ollama": a labelled field ("Any model of the Ollama library, as name or name:tag", placeholder "For example llama3.2:3b") and Pull; Enter pulls too. The pull goes through the same channel, progress, Cancel and stall windows as a Download (`scout:llm.pullOllamaModel`, `LocalModelsStore.download`, `ui/packages/renderer/src/app/core/local-models.store.ts:355`), and the first one also gets the skill-matching model while it is missing, as a Download does. When it ends, Ollama's list is read again, so the model is under Installed, and the field empties.
- **Browse the Ollama library.** A link under the field opens `https://ollama.com/library` through `scout:app.openExternal`, whose allow-list already held `ollama.com` (`ui/packages/main/src/ipc/app.ts:19`; `ui/packages/main/test/ipc-app.test.ts:158`). When the browser cannot be opened the dialog says "Could not open the browser. The library is at ollama.com/library."

Defaults in force unless the owner says otherwise ([[pages/roadmap/ollama-local-models]], "Defaults in force"):

- **What a name is.** `model`, `model:tag` or `namespace/model:tag`: lower-case letters, digits, dots, dashes and underscores, the tag in any case because the library's own tags carry quantization names such as `q4_K_M`, each part at most 80 characters (`isOllamaModelName`, `ui/packages/shared/src/ipc-contract.ts:2364`). A registry host, a second slash, a scheme, spaces and control characters are refused with "That is not a model name. Use name or name:tag, for example llama3.2:3b.", in the dialog before any request and again in main, which also pulls only from an Ollama on this computer (`localhost`, `127.0.0.1` or `[::1]`) and logs no name it refuses (`ui/packages/main/src/ipc/llm-pull.ts:126`, `:130`). A name without a tag is its `:latest`.
- **An installed name is pulled again.** Ollama's pull of an installed tag updates it, so Pull stays Pull and the progress shows.
- **A pull by name never chooses.** It never puts the model in use and never writes `llm.model`; the owner chooses with Use this. A Download of a catalog model keeps making the first model the selection, as before.
- **One vocabulary for a failed pull.** A Download and a Pull by name that fail say "The Ollama library has no model named <name>." when Ollama answers its not-found error line ("pull model manifest: file does not exist"), "Ollama is not answering. Open the Ollama app or run ollama serve, then scan again." when Ollama does not answer, and otherwise "Ollama could not download <name>: " followed by Ollama's own words, each with See logs; Cancel says nothing. Main carries the reason as a code in the message across IPC (`OLLAMA_MODEL_NOT_FOUND`, `llm/ollama-unreachable`, `OLLAMA_PULL_FAILED`; `ui/packages/main/src/ipc/llm-pull.ts:65`, `:82`); before, a failed Download showed Electron's raw rejection. The not-found line was taken from the server's known wording, not observed on this machine, since no pull was made to write this.

No new channel and no new kind of write: the pull is the reviewed local write on the owner's click that [[pages/decisions/0032-per-feature-write-permissions]] already rules on (amendment of 2026-11-11), the same channel and body, and main now accepts less than before. [[pages/services/desktop-ui]] (Ollama models on the Ollama card) carries the current behaviour.

## Third amendment 2026-11-12: a model that cannot call tools says so

Scout answers by calling functions; a local model that cannot call tools answers without ever reading the repositories or the cluster, and until now nothing on the screen said so. Main already reads each listed model's capabilities (`POST /api/show`, amendment above); Ollama's API documentation shows the field in the `/api/show` answer as a list of strings (its example reads `capabilities: ["completion", "vision"]`), and Ollama's own source names the tool-calling capability `tools` (`CapabilityTools`, beside `completion`, `vision` and `embedding`); both were read on 2026-11-12 from the project's main branch, not from the server version on this machine, which has no model installed.

What the owner approved, with the free model list ("go ahead with the free list", translated):

- **Warn, never block.** A model that cannot call tools stays selectable: Use this in "Local models in Ollama" and its row in Models, Analysis still choose it and save `llm.model`.
- **Where it shows.** Its row in the dialog carries a "No tools" chip and, under the row, "This model cannot call tools, so Scout cannot read your repositories or cluster with it. Choose a model that supports tools.", which describes its Use this; its Local LLM row in Models carries the chip; while it is the model in use with Ollama as the provider, or none saved, the LLM Server panel and the In use strip say the sentence.

Defaults in force unless the owner says otherwise ([[pages/roadmap/ollama-local-models]], "Defaults in force"):

- **Only what the server said.** Capabilities Ollama did not report, or that main could not read, say nothing; a catalog model not installed says nothing, since the catalog carries no capability and only an installed model has them.
- **Known and lacking tools warns.** An empty list counts as known, so it warns; an embeddings-only model keeps its own chip and no Use this, and gets no tools warning.

No new call and no new write: the warning reads what main already has. [[pages/services/desktop-ui]] (Ollama models on the Ollama card, Models, Analysis) carries the current behaviour.
