---
title: "Follow-ups"
version: "1.0"
date: 2026-10-21
changes: "Initial tracker page"
page_type: roadmap
status: active
description: "Deferred work found while doing something else: each entry names the blocker, the evidence and what unblocks it."
source:
  - chat
reliability: medium
---

# Follow-ups

> Deferred work found during other changes. Each entry carries its own date; close an entry by deleting it in the change that resolves it.

## Scope

- **In scope:** small, concrete deferrals with a known unblock condition.
- **Out of scope:** initiatives with phases (they get their own page under `pages/roadmap/`).

## Entries

### Semantic Kernel held at 1.78.0 (2026-10-21)

- **Blocker:** SK 1.79 and later require `Microsoft.Extensions.VectorData.Abstractions` >= 10.5.2. `Microsoft.SemanticKernel.Connectors.SqliteVec` 1.74.0-preview, the last version published, is built against 10.1.0. On 10.5.2 `ChunkRecord`'s `VectorStoreVector(Dimensions: ...)` no longer compiles, and after that is fixed, search throws `MissingMethodException` on `VectorSearchOptions.get_OldFilter()` (8 failures in `Scout.Knowledge.Tests`).
- **Unblocks when:** a SqliteVec build targets VectorData >= 10.5, or the vector store in [[pages/services/knowledge]] moves to another connector.
- **Affects:** the whole SK family pinned in `Directory.Packages.props`, per [[pages/decisions/0012-llm-communication-through-semantic-kernel]].

### `electron-builder-squirrel-windows` peer mismatch (2026-10-21)

- **Blocker:** none. `electron-builder` is on 26.18.0, while its unpinned peer `electron-builder-squirrel-windows` stays at 26.8.1 in `ui/bun.lock`, and `bun install` warns about it. The peer is unused while the Windows target in `ui/build/electron-builder.yml:57` stays `nsis`.
- **Fix:** add the peer at the matching version only if a Squirrel target is ever configured.

### Adapter stop has no fault isolation (2026-10-21)

- **Blocker:** none. `ConversationWorker.StopAsync` stops adapters in sequence with no per-adapter `try`/`catch` (`src/Scout.Host.Console/ConversationWorker.cs:69`), so a fault from one adapter's stop (now possible from Slack's awaited `DisconnectAsync`) leaves the adapters after it running until the process exits.
- **Fix:** isolate and log each adapter's stop in the worker, with a test where the first adapter's stop throws and the second is still stopped. A related edge: `SlackChatAdapter.DisposeAsync` calls `StopAsync(CancellationToken.None)`, so a hung disconnect blocks disposal when the host never called `StopAsync`. See [[pages/history/adapter-slack]].

### Synchronous `safeStorage` calls deprecated in Electron 45 (2026-10-21)

- **Blocker:** removal in Electron 46. `ui/packages/main/src/vault/safe-storage-vault.ts` uses the synchronous `safeStorage` encryption API, deprecated in 45.
- **Fix:** move to the async API before taking Electron 46.

### TypeScript 7 deferred (2026-10-21)

- **Blocker:** `typescript-eslint` 8.72 and `@typescript-eslint/typescript-estree` peer `typescript >=4.8.4 <6.1.0`; `@angular/compiler-cli` and `@angular/build` 22.2 peer `>=6.0 <6.1`.
- **Unblocks when:** both widen their TypeScript peer ranges.

### Packaging and native assets only exercised on Linux (2026-10-21)

- **Blocker:** none. Electron 45 packaging (`dist`) and the SQLitePCLRaw 3 native assets for Windows and macOS have not been run; the first run is the `ui-ci.yml` package step on the three-OS matrix.
- **Fix:** watch that CI run, then a packaged smoke run before a release.
