---
title: "Real downloads for local models (Ollama pull + HF Whisper GGML)"
version: "1.0"
date: 2026-05-16
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: adr
status: accepted
description: "The first iteration of \"Manage Downloaded Models\" used mocked state: clicking Download just flipped a flag in localStorage."
source:
  - "obsidian-vault/scout/06-decisions/2026-05-16-local-models-real-downloads.md"
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
