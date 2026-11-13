---
title: "Local Models - operations runbook"
version: "1.6"
date: 2026-05-16
updated: 2026-11-11
changes: "The missing Ollama key is fixed by starting Ollama from its app or a terminal: the LLM Server panel has no Start since 2026-11-11. Earlier: AI Sources has no Local Models card any more, so its Connected row is gone (2026-11-11).."
page_type: runbook
status: active
description: "Quick reference for working with the AI Sources / Models surface in the Scout Desktop app."
source:
  - "owner-notes/05-operations/local-models-runbook.md"
reliability: medium
---

# Local Models - operations runbook

Quick reference for working with the AI Sources / Models surface in
the Scout Desktop app. Decisions are recorded under
[[pages/decisions/0007-ai-sources-screen]] and
[[pages/decisions/0008-local-models-real-downloads]]; this doc is the
"what do I do when X is broken" companion.

## File locations

| What                            | Path |
|---------------------------------|------|
| Ollama model store              | `~/.ollama/models/` |
| Ollama identity (Ollama creates it at its first start; Scout never writes it) | `~/.ollama/id_ed25519` |
| Whisper GGML store (Linux)      | `~/.config/Scout Desktop/whisper-models/` |
| Whisper GGML store (macOS)      | `~/Library/Application Support/Scout Desktop/whisper-models/` |
| Whisper GGML store (Windows)    | `%APPDATA%\Scout Desktop\whisper-models\` |
| Renderer state mirror           | `localStorage['scout:local-models']` (DevTools → Application → Local Storage) |
| Latest AppImage build           | `~/WorkSpace/scout/ui/build/dist/Scout-Desktop-<version>-linux-x86_64.AppImage` |

## How a Connected badge is computed

| Card           | Connected when |
|----------------|----------------|
| **Ollama**     | `GET http://localhost:11434/api/tags` returns 2xx |
| **LM Studio**  | `GET http://localhost:1234/v1/models` returns 2xx |
| **Cloud (all)** | Always Not connected (placeholder cards) |

If a runtime is running but the badge stays Not connected, the HTTP
endpoint is not reachable from inside the Electron sandbox. Confirm
with `curl http://localhost:11434/api/tags` on a host shell.

## Common failure modes

### `pull model manifest: open ~/.ollama/id_ed25519: no such file or directory`

Ollama refuses to pull because its identity key has never been
generated, which means its server has never run on this machine.
Since 2026-11-08 Scout does not create the key (it is a write
outside Scout's folders, [[pages/decisions/0008-local-models-real-downloads]]
amendment), and since 2026-11-11 it does not start Ollama either:
the LLM Server panel has no Start.

1. Start Ollama once: the Ollama app, or `ollama serve` in a terminal.
   Ollama writes `~/.ollama/id_ed25519` itself as it starts.
2. Retry the download from the row.

### `model not found` from Ollama

The tag is not in the registry. Check the catalog → tag mapping in
`local-models.component.ts` against
[ollama.com/library](https://ollama.com/library). Known recent
correction: `qwen-3-vl-2b` was originally mapped to `qwen2.5vl:3b`
(non-existent); now points to `qwen2.5:3b`.

### A model that is not installed yet or still downloading

Symptom: a chat message sent while the chat model is still downloading, or
before it was downloaded, is answered "The AI Source does not have the model
<id>. A local model is not installed yet or is still downloading: check AI
Sources in Scout Desktop." A live translation line shows the same sentence
with the translation model's id, and an audit pauses with "the AI Source does
not have the model <id>". The worker log has one Warning naming the model.
Ollama answered `404 {"error":{"message":"model '<id>' not found","type":"not_found_error",...}}`
([[pages/services/llm]]).

When the embedding model is the one missing, the worker logs once:
`The embedding model <id> is not installed on the AI Source (HTTP 404);
skills are picked by the words of the message until it is. Download it in
Scout Desktop, AI Sources.` Skills are still picked by the words of the
message ([[pages/services/skills]]); the first message a minute or more
after the last attempt builds the index again.

Fix: download the model on the AI Sources screen, or let its download
finish, then send the message again; Scout does not resend it. For an
audit, ask Scout to continue it once the model is there.

### Whisper download stalls or 4xx

The Hugging Face URL is hard-coded to
`ggerganov/whisper.cpp/resolve/main/<file>.bin`. If HF changes path
shape, this breaks. Check the URL in
`packages/main/src/ipc/whisper.ts`. Hugging Face also throttles
unauthenticated downloads occasionally; the user can retry.

### UI shows model as downloaded but disk doesn't

The local store flag and the disk state can diverge if:
- The user deleted the file outside the app.
- An update changed the storage layout.

To resync **Whisper**: re-launch the app. `ngOnInit` calls
`whisperListInstalled()` which reconciles the store against actual
files on disk.

For **Ollama**: there is no automatic reconciliation today. Manual fix:

```bash
# inside DevTools console
localStorage.setItem('scout:local-models',
  JSON.stringify({ downloaded: [], selected: { transcription: null, analysis: null } }))
```

…then redo downloads from the UI.

### `ollama.service` crash-loops (`mkdir /usr/share/ollama: permission denied`)

Symptom (seen 2026-07-03, 10,816 restarts): `systemctl is-active ollama`
stuck on `activating`; `journalctl -u ollama` shows
`Error: could not create directory mkdir /usr/share/ollama: permission denied`.
In the app this surfaces as `LLM provider unavailable` on every chat reply
and `Failed to build skill index; picking skills by the words of the message
until a retry after <time>.` (embeddings share the endpoint).

Cause: the systemd unit runs as user `ollama` whose home
(`/usr/share/ollama`) does not exist.

Fix:

```bash
sudo mkdir -p /usr/share/ollama
sudo chown ollama:ollama /usr/share/ollama
```

Caveat: the service user then stores models under
`/usr/share/ollama/.ollama/models`, NOT the user-level `~/.ollama/models`
(which may hold many GB of already-pulled models). Either add
`Environment="OLLAMA_MODELS=/home/<user>/.ollama/models"` to the unit
(and grant the ollama user read access) or re-pull the models needed
(e.g. `qwen2.5:3b`).

### Connected badge stays "Not connected" after install

Symptom: user installed Ollama or LM Studio after opening the app;
badge does not refresh.

Fix: click the **Scan for X** button on the card — that re-runs the
endpoint probe. There is no automatic re-scan on focus or interval.

## Where to find error details

1. **Per-row inline error**: shows up under the offending model
   row in the Manage Downloaded Models screen.
2. **Logs view** (`/logs`): every download failure also appends to
   `LogStore` via `appendDiagnostic`, so the verbatim error text
   (e.g. `pull model manifest: ...`) is searchable there alongside
   worker stdout/stderr.
3. **DevTools console**: open with `Ctrl+Shift+I` (Linux/Windows) or
   `Cmd+Opt+I` (macOS). The main process logs through Electron's
   logger; renderer-side exceptions also surface here.
4. **AppImage stdout**: if launched from a terminal, the main
   process logger writes structured logs to stderr.

## Forcing a fresh build

```bash
cd ~/WorkSpace/scout/ui
bun run typecheck
bun run test
bun run dist:linux
```

Artifact lands at
`build/dist/Scout-Desktop-<version>-linux-x86_64.AppImage`. Mark
executable on first run:

```bash
chmod +x build/dist/Scout-Desktop-0.0.1-linux-x86_64.AppImage
```

## What this runbook does not cover

- Worker-side transcription / inference. Not built yet.
- Cloud provider authentication. Not built yet.
- Multi-user / multi-profile isolation. Storage is per-OS-user.
