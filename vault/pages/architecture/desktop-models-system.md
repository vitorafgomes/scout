---
title: "Desktop models system (AI Sources → Models)"
version: "1.0"
date: 2026-05-16
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: longform
status: active
description: "How the Scout Desktop pieces fit together to discover an inference backend (AI Sources), download a model into it, and select which model to use."
source:
  - "obsidian-vault/scout/02-architecture/desktop-models-system.md"
reliability: medium
---

# Desktop models system (AI Sources → Models)

## Scope

This document explains how the Scout Desktop pieces fit together to
let a user **discover an inference backend** (AI Sources), **download
a model into it** (Manage Downloaded Models), and then **select which
model handles which task** (Models). Decisions are recorded in
[[pages/decisions/0006-models-screen-task-based]], [[pages/decisions/0007-ai-sources-screen]]
and [[pages/decisions/0008-local-models-real-downloads]].

## Information architecture

The post-connect side-nav is ordered top to bottom:

```
AI Sources       /sources
  └── Manage…    /sources/local-models  (drill-in from a card)
Models           /llm                  (route name kept for compat)
Status           /status
Logs             /logs
Settings         /settings
```

- **AI Sources** is the lower layer — *where do models come from*.
- **Models** is the upper layer — *which model handles which task*.

A user with zero models installed naturally reads top-down: AI Sources
shows Ollama / LM Studio status, the "Manage Downloaded Models"
button takes them to the catalogue, they download something, then
Models surfaces it for selection.

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

`LlmComponent` still exists in the codebase but is **not mounted on
any route**. It is candidate orphan code pending a cleanup PR.

## State stores

### `LocalModelsStore`
`providedIn: 'root'`, persists to `localStorage['scout:local-models']`.
Single source of truth for:

- `downloaded: readonly string[]` — catalog ids the user has marked
  downloaded (or that `whisperListInstalled` reconciled in on app
  start).
- `selected: { transcription: string | null; analysis: string | null }`
  — the active model per kind. Auto-assigned on first download of
  each kind so the user is never in an "I downloaded one model but
  nothing is selected" state.

API: `isDownloaded`, `downloadedFor(kind)`, `catalogFor(kind)`,
`selectedFor(kind)`, `markDownloaded`, `markRemoved`, `select`,
`deleteUnused`, `totalDownloadedBytes`.

### Orphan stores (pending removal)

- `LlmConfigStore` — used to back the old `LlmComponent`. Replaced by
  `LocalModelsStore` selection.
- `TranscriptionConfigStore` — reserved `stt.provider` / `stt.model`
  vault keys for an earlier draft. Never consumed by the worker.

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

## IPC surface

All channels are declared in `@scout/shared`'s `CHANNEL` constant and
typed on the `ScoutBridge` interface.

### Reads (idempotent, safe)

| Channel                          | Direction          | Purpose |
|----------------------------------|--------------------|---------|
| `scout:llm.listOllamaModels`     | renderer → main    | `GET /api/tags` |
| `scout:llm.listLmStudioModels`   | renderer → main    | `GET /v1/models` |
| `scout:whisper.listInstalled`    | renderer → main    | Read `userData/whisper-models/` |

### Writes (all local, allow-listed)

| Channel                          | Direction          | Purpose |
|----------------------------------|--------------------|---------|
| `scout:llm.pullOllamaModel`      | renderer → main    | `POST /api/pull` stream |
| `scout:llm.deleteOllamaModel`    | renderer → main    | `DELETE /api/delete` |
| `scout:whisper.downloadModel`    | renderer → main    | HF streaming download |
| `scout:whisper.deleteModel`      | renderer → main    | `fs.rm` on userData |

### Events (push)

| Channel                          | Direction          | Purpose |
|----------------------------------|--------------------|---------|
| `scout:llm.pullOllamaProgress`   | main → renderer    | NDJSON-derived percent |
| `scout:whisper.downloadProgress` | main → renderer    | Content-Length-derived percent |

Every "write" channel was reviewed against the read-only invariant
defined in [[pages/decisions/0001-platform-priority]] and added to
`LOCAL_WRITE_ALLOWLIST` with a justification comment in
`shared/test/ipc-contract.test.ts`. Ollama and Whisper models live on
the user's own disk; nothing leaves the machine through these
handlers.

## End-to-end flow: downloading Qwen 2.5 3B

```
User clicks Download in /sources/local-models (Local Analysis tab)
  ↓
LocalModelsComponent.onDownload('qwen-3-vl-2b')
  ↓
ollamaTagFor('qwen-3-vl-2b') → 'qwen2.5:3b'
  ↓
realPull('qwen-3-vl-2b', 'qwen2.5:3b')
  ↓ requestId = 'qwen-3-vl-2b:<timestamp>'
  ↓
bridge.llmPullOllamaModel({ endpoint, tag, requestId })
  ↓ IPC invoke
main.ipc.llm-pull handler
  ↓ ensureOllamaIdentity()  (ssh-keygen if missing)
  ↓ POST http://localhost:11434/api/pull { name: 'qwen2.5:3b', stream: true }
  ↓ reader = response.body.getReader()
  ↓ per chunk: parse NDJSON line, emit
  ↓   { kind: 'event', channel: 'scout:llm.pullOllamaProgress',
        payload: { requestId, percent, completedBytes, totalBytes } }
  ↓ on { status: 'success' }: resolve
  ↑ IPC return
LocalModelsComponent.finishDownload('qwen-3-vl-2b')
  ↓ store.markDownloaded → auto-selects if first of kind
  ↓ ModelsComponent now lists it in Analysis tab
```

## End-to-end flow: downloading Whisper Large v3

```
User clicks Download in /sources/local-models (Local Transcriptions tab)
  ↓
LocalModelsComponent.onDownload('whisper-large-v3')
  ↓
whisperVariantFor('whisper-large-v3') → 'large-v3'
  ↓
realWhisperDownload('whisper-large-v3', 'large-v3')
  ↓ requestId = 'whisper:whisper-large-v3:<timestamp>'
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
LocalModelsComponent.finishDownload('whisper-large-v3')
  ↓ store.markDownloaded
  ↓ ModelsComponent now lists it in Transcription tab
```

## Storage layout

| What                          | Where |
|-------------------------------|-------|
| Ollama models                 | Ollama's own data dir (e.g. `~/.ollama/models/`) |
| Ollama identity key (auto)    | `~/.ollama/id_ed25519` |
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
