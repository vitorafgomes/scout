---
title: "Local Models - operations runbook"
version: "1.0"
date: 2026-05-16
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: runbook
status: active
description: "Quick reference for working with the AI Sources / Models surface in the Scout Desktop app."
source:
  - "obsidian-vault/scout/05-operations/local-models-runbook.md"
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
| Ollama identity (auto-gen)      | `~/.ollama/id_ed25519` |
| Whisper GGML store (Linux)      | `~/.config/Scout Desktop/whisper-models/` |
| Whisper GGML store (macOS)      | `~/Library/Application Support/Scout Desktop/whisper-models/` |
| Whisper GGML store (Windows)    | `%APPDATA%\Scout Desktop\whisper-models\` |
| Renderer state mirror           | `localStorage['scout:local-models']` (DevTools → Application → Local Storage) |
| Latest AppImage build           | `~/WorkSpace/scout/ui/build/dist/Scout Desktop-<version>-linux-x86_64.AppImage` |

## How a Connected badge is computed

| Card           | Connected when |
|----------------|----------------|
| **Ollama**     | `GET http://localhost:11434/api/tags` returns 2xx |
| **LM Studio**  | `GET http://localhost:1234/v1/models` returns 2xx |
| **Local Models** | Ollama **or** LM Studio is Connected **and** `LocalModelsStore.selected` has at least one non-null value |
| **Cloud (all)** | Always Not Connected (placeholder cards) |

If a runtime is running but the badge stays Not Connected, the HTTP
endpoint is not reachable from inside the Electron sandbox. Confirm
with `curl http://localhost:11434/api/tags` on a host shell.

## Common failure modes

### `pull model manifest: open ~/.ollama/id_ed25519: no such file or directory`

Ollama refuses to pull because its identity key has never been
generated. **The app handles this automatically** since the
2026-05-16 build: before the first pull the main process runs
`ssh-keygen -t ed25519 -f ~/.ollama/id_ed25519 -N "" -q`.

If this still fires after the upgrade:
1. Check `ssh-keygen` is on PATH (`which ssh-keygen` should resolve).
   If missing, install `openssh-client` (Linux) or rely on Windows
   10+'s built-in OpenSSH.
2. Delete any half-written file at `~/.ollama/id_ed25519` (a
   zero-byte file can confuse the existence check).
3. Re-launch the app and retry the download.

### `model not found` from Ollama

The tag is not in the registry. Check the catalog → tag mapping in
`local-models.component.ts` against
[ollama.com/library](https://ollama.com/library). Known recent
correction: `qwen-3-vl-2b` was originally mapped to `qwen2.5vl:3b`
(non-existent); now points to `qwen2.5:3b`.

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
and `Failed to build skill index` (embeddings share the endpoint).

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

### Connected badge stays "Not Connected" after install

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
`build/dist/Scout Desktop-<version>-linux-x86_64.AppImage`. Mark
executable on first run:

```bash
chmod +x "build/dist/Scout Desktop-0.0.1-linux-x86_64.AppImage"
```

## What this runbook does not cover

- Worker-side transcription / inference. Not built yet.
- Cloud provider authentication. Not built yet.
- Multi-user / multi-profile isolation. Storage is per-OS-user.
