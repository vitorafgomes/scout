---
title: "Desktop models system (AI Sources → Models)"
version: "1.11"
date: 2026-05-16
updated: 2026-11-12
changes: "v1.11 (2026-11-12) a model whose known capabilities lack tools is marked No tools, and the model in use says it; choosing stays allowed.."
page_type: longform
status: active
description: "How the Scout Desktop pieces fit together to discover an inference backend (AI Sources), download a model into it, and select which model to use."
source:
  - "owner-notes/02-architecture/desktop-models-system.md"
reliability: medium
---

# Desktop models system (AI Sources → Models)

## Scope

This document explains how the Scout Desktop pieces fit together to let a user **discover an inference backend** (AI Sources), **download a model into it** (the Ollama card's Manage local models, or Models, Transcription for the Whisper models), and then **select which model handles which task** (Models). Decisions are recorded in [[pages/decisions/0006-models-screen-task-based]], [[pages/decisions/0007-ai-sources-screen]] and [[pages/decisions/0008-local-models-real-downloads]].

## Information architecture

The post-connect side-nav is ordered top to bottom:

```
Chat             /chat
AI Sources       /sources
Models           /llm                  (route name kept for compat)
Features         /features
Logs             /logs                 (hidden when Settings turns it off)
Settings         /settings             (worker status and controls live here)
```

- **AI Sources** is the lower layer — *where do models come from*.
- **Models** is the upper layer — *which model handles which task*.

A user with zero models installed naturally reads top-down: AI Sources shows Ollama / LM Studio status, the Ollama card's "Manage local models" opens the Ollama catalogue, they download something, then Models surfaces it for selection.

## Component map

```
PostConnectShellComponent
├── SourcesComponent              (/sources)
└── LocalModelsComponent          (/sources/local-models)
ModelsComponent                   (/llm)
├── (Transcription panel — inline)
├── (Analysis panel — inline)
└── (Translation panel — coming soon)
```

## State stores

### `LocalModelsStore`
`providedIn: 'root'`, persists to `localStorage['scout:local-models']`.
Single source of truth for:

- `downloaded: readonly string[]` — catalog ids the user has marked
  downloaded (or that `whisperListInstalled` reconciled in on app
  start), and the tag of every Ollama model outside the catalog that
  Ollama listed (2026-11-12).
- `selected: { transcription: string | null; analysis: string | null }`
  — the active model per kind. Auto-assigned on first download of
  each kind so the user is never in an "I downloaded one model but
  nothing is selected" state.

API: `isDownloaded`, `downloadedFor(kind)`, `catalogFor(kind)`,
`selectedFor(kind)`, `markDownloaded`, `markRemoved`, `select`,
`deleteUnused`, `totalDownloadedBytes`, `refreshOllama`, `ollamaAnswers`,
`isEmbeddingsOnly`, `detailsOf`.

`downloaded` also holds `nomic-embed-text`, the skill-matching model,
while Ollama has it. `refreshOllama`
(`ui/packages/renderer/src/app/core/local-models.store.ts:437`) reads
`scout:llm.listOllamaModels` when the Ollama dialog opens and every 4 s
while it is open, one read for the LLM Server panel and this row: on
an answer the Ollama ids in `downloaded` become exactly what Ollama
lists, in its order: a catalog model under its catalog id, this id only
when a listed name is `nomic-embed-text` or `nomic-embed-text:latest`,
the model the worker asks for, and every other model under its tag as
listed (2026-11-12), and `ollamaAnswers` is true; a call while a read is in flight returns that read; when the list cannot be read
`downloaded` is kept and `ollamaAnswers` is false (2026-11-11). It
never selects a model and writes nothing to the vault; `openOllama`
sets `ollamaAnswers` to null before the read, when the dialog opens. It has no kind, so it is
never selected and never written to `llm.model`.

**Models outside the catalog (2026-11-12).** The owner asked that the
Ollama dialog list every installed model and let him choose any of
them. A model outside the catalog is keyed by its tag as Ollama lists
it (`name:tag`, always with a colon; the catalog ids never have one, so
the two key spaces cannot collide). `lookup` builds its entry, titled by
the tag, with the size Ollama listed and no description;
`ollamaTagForCatalogId` returns the key itself for such a key and null
for a Whisper id, and `catalogIdForOllamaTag` returns the tag when the
catalog has no entry for it (`ui/packages/renderer/src/app/core/local-models.catalog.ts`).
The rows' details (family, parameter size, quantization, capabilities)
live in memory for the window; the keys persist, so a reload while Ollama is down still lists the models and the selection. `select`
saves the tag itself as `llm.model`, which `buildEnv` passes unchanged
to the worker as `SCOUT__LLM__OLLAMA__CHAT__MODEL` and which the unload
on a provider switch reads as Scout's chat model; a stored catalog id
keeps working, since the catalog ids stay valid keys.

**Embeddings-only models.** `isEmbeddingsOnly` is true when the
capabilities main read list `embedding` without `completion`; when
Ollama did not report them, when the name matches `/embed/i` or the
family `/bert/i`. That fallback is a naive heuristic: a chat model whose
name says embed is refused, and an embeddings model named otherwise is
offered; its upgrade is to trust only the capabilities once every
server in use reports them. `select` refuses such a model with "<tag>
only turns text into embeddings; it cannot answer in chat." and writes
nothing, and `markRemoved`'s fallback skips it. In the dialog it is a
row with an "Embeddings only" chip, Remove and no Use this; in Models
it is never a row. **No tools:** `cannotCallTools` is true only for capabilities Ollama reported that lack `tools`, on a model not embeddings-only; unknown capabilities say nothing.
Its row in the dialog and in Models carries "No tools", and while it is in use with Ollama the LLM Server panel and the In use strip say it cannot call tools; Use this
and the row still choose it ([[pages/decisions/0008-local-models-real-downloads]], third amendment of 2026-11-12).

### Orphan stores (pending removal)

- `LlmConfigStore` — used to back the old `LlmComponent`. Replaced by
  `LocalModelsStore` selection.
- `TranscriptionConfigStore` was removed on 2026-11-08: it wrote
  `stt.model` and had no caller, and only main writes `stt.model`.

## Static catalog

`core/local-models.catalog.ts` is a frozen array of
`LocalModelCatalogEntry { id, kind, title, description, badges,
sizeBytes, ramBytes }`. Source of truth for what the UI renders.

Current entries:

| id                          | kind          | label                  | backend |
|-----------------------------|---------------|------------------------|---------|
| `whisper-large-v3`          | transcription | Whisper Large v3       | HF GGML |
| `whisper-large-v3-turbo`    | transcription | Whisper Large v3 Turbo | HF GGML |
| `qwen-2.5-0.5b-instruct`    | analysis      | Qwen 2.5 0.5B (Instruct) | Ollama |
| `qwen-2.5-1.5b-instruct`    | analysis      | Qwen 2.5 1.5B (Instruct) | Ollama |
| `qwen-3-vl-2b` (legacy id)  | analysis      | Qwen 2.5 3B            | Ollama |
| `gemma-3-4b`                | analysis      | Gemma 3 4B             | Ollama |
| `gemma-3-12b`               | analysis      | Gemma 3 12B            | Ollama |

Beside the array, `SKILL_MODEL`
(`ui/packages/renderer/src/app/core/local-models.catalog.ts:127`) is
the skill-matching model: id and Ollama tag both `nomic-embed-text`,
the shared `OLLAMA_EMBEDDING_MODEL`, and 274 MB, the Ollama library's
figure. It is in the tag map but not in `LOCAL_MODEL_CATALOG`, so it is
never a chat choice and the DOWNLOADED and STORAGE metrics leave it
out.

## IPC surface

All channels are declared in `@scout/shared`'s `CHANNEL` constant and
typed on the `ScoutBridge` interface.

### Reads (idempotent, safe)

| Channel                          | Direction          | Purpose |
|----------------------------------|--------------------|---------|
| `scout:llm.listOllamaModels`     | renderer → main    | `GET /api/tags`, then one `POST /api/show {model}` per listed model, 2 s each, concurrent, kept by digest once read (a 200 with no `capabilities` is kept as unknown); a failed or timed-out show leaves that model's capabilities unknown, is not asked again for 60 s, logs one warning naming the model, and never fails the list |
| `scout:llm.listLmStudioModels`   | renderer → main    | `GET /v1/models` |
| `scout:whisper.listInstalled`    | renderer → main    | Read `userData/whisper-models/` |

### Writes (all local, allow-listed)

| Channel                          | Direction          | Purpose |
|----------------------------------|--------------------|---------|
| `scout:llm.pullOllamaModel`      | renderer → main    | `POST /api/pull` stream, only for a model name of the library (`isOllamaModelName`) and only from an Ollama on this computer, both checked before any request (2026-11-12) |
| `scout:llm.deleteOllamaModel`    | renderer → main    | `DELETE /api/delete` |
| `scout:llm.unloadOllamaModels`   | renderer → main    | `GET /api/ps`, then `POST /api/generate` with a zero keep-alive for each loaded model whose tag is one of Scout's, on the Ollama endpoint configured in Scout (localhost by default, the one chat uses), when the provider in use moves away from Ollama; Ollama keeps no record of which program loaded a model |
| `scout:whisper.downloadModel`    | renderer → main    | HF streaming download |
| `scout:whisper.deleteModel`      | renderer → main    | `fs.rm` on userData |

### Events (push)

| Channel                          | Direction          | Purpose |
|----------------------------------|--------------------|---------|
| `scout:llm.pullOllamaProgress`   | main → renderer    | NDJSON-derived percent |
| `scout:whisper.downloadProgress` | main → renderer    | Content-Length-derived percent |

Every "write" channel was reviewed against the write contract ([[pages/decisions/0032-per-feature-write-permissions]], which superseded the read-only invariant; Scout writes on the host only inside its own folders) and added to `LOCAL_WRITE_ALLOWLIST` with a justification comment in `shared/test/ipc-contract.test.ts`. Ollama and Whisper models live on the user's own disk; nothing leaves the machine through these handlers.
`scout:llm.unloadOllamaModels` (2026-11-12) is listed there without a
comment, since the project's code carries no comments: its
justification is its row above and the bullet of 2026-11-12 in
[[pages/decisions/0032-per-feature-write-permissions]]. It deletes
nothing; Ollama loads the model again on the next request from any
program, and a model loaded under one of Scout's tags is unloaded
whoever loaded it.

## End-to-end flow: downloading Qwen 2.5 3B

```
User clicks Download in /sources/local-models (Local Analysis tab)
  ↓
LocalModelsComponent.onDownload('qwen-3-vl-2b')
  ↓
LocalModelsStore.download('qwen-3-vl-2b')   (progress kept in the store)
  ↓ ollamaTagForCatalogId → 'qwen2.5:3b', requestId = 'qwen-3-vl-2b:<timestamp>'
  ↓
bridge.llmPullOllamaModel({ endpoint, tag, requestId })
  ↓ IPC invoke
main.ipc.llm-pull handler   (no identity key: Ollama creates its own)
  ↓ POST http://localhost:11434/api/pull { name: 'qwen2.5:3b', stream: true }
  ↓ reader = response.body.getReader()
  ↓ per line: parse NDJSON, emit
  ↓   { kind: 'event', channel: 'scout:llm.pullOllamaProgress',
        payload: { requestId, percent, completedBytes, totalBytes } }
  ↓ 60 s stall window while lines carry byte counts; a 10-minute one
  ↓   while lines carry only a status (pulling manifest, verifying
  ↓   sha256 digest, writing manifest)
  ↓ on { status: 'success' }: resolve; an error line, an early end,
  ↓   a stall or scout:llm.cancelOllamaPull: reject
  ↑ IPC return
LocalModelsStore.download
  ↓ store.markDownloaded → auto-selects if first of kind
  ↓ Ollama's list read again once any read in flight settled
  ↓ ModelsComponent now lists it in Analysis tab
```

From the "Local models in Ollama" dialog the same click also starts
`LocalModelsStore.download('nomic-embed-text')`, the skill-matching
model, right after the chat model's pull: a second pull over the same
channel with its own request id, which the store ignores when the model
is downloaded or already downloading
(`ui/packages/renderer/src/app/sources/local-models.component.ts:1362`).
Its failure or Cancel shows on its own row and never stops the chat
model's pull.

**Pull by name (2026-11-12).** The dialog's field takes any model of
the library, trimmed and checked by `isOllamaModelName` before the
bridge, and keys it as the list read would (`ollamaKeyForName`: no tag
means `:latest`, the skill model's name its id, a catalog tag its id).
It calls `download(key, { byName: true })`, the same path, which pulls
an installed model again and never calls `markDownloaded`, so it
selects nothing. Main reads the not-found line, a refused connection
and every other failure as codes the dialog turns into one set of
sentences for a Download and a pull by name ([[pages/services/desktop-ui]]). The worker is told the same name as
`SCOUT__LLM__OLLAMA__EMBEDDINGS__MODEL`
(`ui/packages/main/src/worker/build-env.ts:155`).

## End-to-end flow: downloading Whisper Large v3

```
User clicks Download in /sources/local-models (Local Transcriptions tab)
  ↓
LocalModelsComponent.onDownload('whisper-large-v3')
  ↓
LocalModelsStore.download('whisper-large-v3')   (progress kept in the store)
  ↓ variant 'large-v3', requestId = 'whisper-large-v3:<timestamp>'
  ↓
bridge.whisperDownloadModel({ variant: 'large-v3', requestId })
  ↓ IPC invoke
main.ipc.whisper handler
  ↓ mkdir <userData>/whisper-models  (mode 700)
  ↓ GET https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3.bin
  ↓ totalBytes ← Content-Length
  ↓ reader = response.body.getReader()
  ↓ open write stream → ggml-large-v3.bin.partial
  ↓ per chunk: out.write, emit progress (capped at 99)
  ↓ on stream end: out.end → rename .partial → .bin → emit 100
  ↑ IPC return { variant, path }
LocalModelsStore.download
  ↓ store.markDownloaded
  ↓ ModelsComponent now lists it in Transcription tab
```

## Storage layout

| What                          | Where |
|-------------------------------|-------|
| Ollama models                 | Ollama's own data dir (e.g. `~/.ollama/models/`) |
| Ollama identity key           | `~/.ollama/id_ed25519`, created by Ollama at its first start, never by Scout |
| Whisper GGML models           | `<userData>/whisper-models/ggml-*.bin` |
| Renderer state mirror         | `localStorage['scout:local-models']` |

`<userData>` per platform:
- Linux: `~/.config/Scout Desktop/`
- macOS: `~/Library/Application Support/Scout Desktop/`
- Windows: `%APPDATA%\Scout Desktop\`

## Cross-cutting concerns

- **Read-only invariant**: every IPC channel either reads remote APIs
  the user has running on their own machine, or writes only to the
  user's own filesystem. No mutation against external services.
- **CSP**: external URL navigation (Download Ollama, Download LM
  Studio) goes through `appOpenExternal`, which validates against a
  main-process allow-list before calling `shell.openExternal`.
- **Error surfacing**: see [[pages/decisions/0008-local-models-real-downloads]]
  — every download failure writes to both the per-row inline error
  component and `LogStore` so the message shows up under `/logs`.

## What is **not** built yet

- Actual transcription / inference. The downloads are real but the
  .NET worker does not yet load these files. Pending separate ADRs.
- Worker reading `LocalModelsStore.selected` on spawn. Currently the
  worker still pulls `llm.*` from the vault, but those keys are
  decoupled from this screen.
- Cloud providers (the five cards on the right of AI Sources).
- The defects of the local-model flow found on 2026-11-11 and the lanes that fix them are tracked in [[pages/roadmap/ollama-local-models]].
