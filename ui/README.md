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

## Development

```sh
bun run dev:main            # launch Electron in dev mode (requires built preload + renderer)
bun run worker:publish      # dotnet publish Scout.Host.Worker for the current RID
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

## CI

Every PR against `main` that touches `ui/**` or the worker runs the
UI CI workflow (`.github/workflows/ui-ci.yml`) on Ubuntu, macOS, and
Windows. It lints, typechecks, tests, publishes the .NET worker, and
packages unsigned artifacts. Artifacts can be downloaded from the
workflow run page.

## Packaging

Packaging uses electron-builder. Artifacts are UNSIGNED in this repo —
do not distribute them as official releases without proper signing.

Prerequisites:

- Full workspace build: `bun install`, then `bun run package:all`
- .NET SDK for the worker publish step

Commands:

- `bun run dist` — packages for the current host platform.
- `bun run dist:linux` — AppImage + .deb, x64.
- `bun run dist:mac` — DMG, arm64 + x64.
- `bun run dist:win` — NSIS installer, x64.

Outputs land under `ui/build/dist/`.

AppImage is always produced on Linux builds — this is a release invariant.

Icons under `build/icons/` are placeholders; replace before 1.0.

## Architecture

See the plan document linked above for the full architecture diagram, IPC contract,
PR breakdown, and security posture.
