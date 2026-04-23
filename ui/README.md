# Scout Desktop UI

Scout Desktop Electron UI — provisioning wizard for the Scout bot.

Design doc and plan:
`obsidian-vault/scout/05-operations/plans/2026-04-23-scout-desktop-m1.md`

## Requirements

- [Bun](https://bun.sh/) >= 1.2.0
- Node 24 (Electron 41's bundled runtime; use `.nvmrc` with nvm for CI parity)

## Install

```sh
cd ui/
bun install
```

## Workspace packages

| Package                   | Description                                         |
| ------------------------- | --------------------------------------------------- |
| `@scout/shared`           | Shared TypeScript types and IPC contract            |
| `@scout/desktop-main`     | Electron main process                               |
| `@scout/desktop-preload`  | Electron preload script (contextBridge)             |
| `@scout/desktop-renderer` | Angular 21 renderer (standalone, Signals, zoneless) |
| `@scout/desktop-e2e`      | Playwright E2E tests (added in PR 13)               |

## Commands

```sh
bun install              # install all workspace deps
bun run lint             # ESLint across packages/
bun run typecheck        # tsc --noEmit per package
bun run test             # unit tests across shared, main, preload, renderer
bun run build            # build shared, preload, renderer, main in order
bun run format           # Prettier check
bun run format:write     # Prettier write
bun run clean            # remove dist/ and node_modules/
bun run worker:publish   # dotnet publish Scout.Host.Worker for the current RID
                         # Output: ui/build/worker/<rid>/Scout.Host.Worker[.exe]
                         # Windows: use build/scripts/publish-worker.ps1 directly
```

Build ordering: run `bun run --cwd packages/preload build`, then
`bun run --cwd packages/renderer build`, then `bun run --cwd packages/main build`
before `dev:main`. The main process resolves the preload from
`packages/preload/dist/preload.cjs` and the renderer index from
`packages/renderer/dist/browser/index.html` at startup; without the built
artifacts the window opens without the `window.scout` bridge or with a blank
page (a warning is logged but Electron still launches). `bun run build` at the
workspace root chains the four builds in the correct order.

The renderer is Angular 21 with standalone components, Signals, zoneless change
detection, and the new control flow (`@if` / `@for`). Router runs in hash mode
so the bundled `index.html` loads correctly from the filesystem. Tests use
Vitest via `@analogjs/vite-plugin-angular`; zone.js is intentionally absent —
`TestBed` is configured with `provideZonelessChangeDetection()` per test.

Bun is the runtime for all workspace commands. Node is only required on the host
for `@electron/rebuild` (native modules); install the version pinned in `.nvmrc`
if you plan to run `bun run rebuild` locally. Contributors who only touch UI code
do not need Node at all.

## Architecture

See the plan document linked above for the full architecture diagram, IPC contract,
PR breakdown, and security posture.
