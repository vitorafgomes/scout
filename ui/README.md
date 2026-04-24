# Scout Desktop UI

Scout Desktop is the Electron provisioning UI for the Scout read-only
chatops bot. It detects installed chat clients, runs per-platform
provisioning wizards, and supervises the local `.NET` worker as a child
process.

## Stack

- Bun 1.3+ — workspace toolchain (install, scripts, tests).
- Electron 41 — desktop shell (main process in ESM, sandboxed preload).
- Angular 21 — renderer, zoneless change detection, Signals-first,
  standalone components with the new `@if`/`@for` control flow.
- .NET 10 — the `Scout.Host.Worker` spawned as a supervised child
  process.
- electron-builder — cross-platform packaging (NSIS, DMG, AppImage,
  deb).

## Prerequisites

- Bun 1.3 or newer — install from `https://bun.sh/` (`curl -fsSL
  https://bun.sh/install | bash`).
- .NET SDK matching the version in `../global.json` (currently 10.x).
- Optional: [Ollama](https://ollama.com/) if you want a local LLM
  provider without paid credentials.

`.nvmrc` pins the Node major that Electron ships with. It is guidance
for contributors who also keep Node installed for tooling; Bun drives
every workspace command, so Node is not required to build or test the
UI.

## Install

```sh
cd ui
bun install
```

## Run in dev

Open two terminals from `ui/`:

```sh
# Terminal 1 — Angular dev server with HMR on port 4200
bun run --cwd packages/renderer dev
```

```sh
# Terminal 2 — build preload + main, then launch Electron
bun run --cwd packages/preload build
bun run --cwd packages/main build
bun run --cwd packages/main dev:main
```

The main process resolves the preload from
`packages/preload/dist/preload.cjs` and the renderer from
`packages/renderer/dist/browser/index.html` (prod) or from
`http://localhost:4200` when `NODE_ENV=development`. `bun run build` at
the workspace root chains `shared → preload → renderer → main` in the
correct order if you prefer a single command.

## Test

```sh
bun run test          # shared + main + preload + renderer
```

.NET tests are separate — run them from the repo root:

```sh
dotnet test Scout.slnx
```

## Package

Artifacts land under `ui/build/dist/`.

```sh
bun run dist          # package for the current host
bun run dist:linux    # AppImage + .deb, x64
bun run dist:mac      # DMG, arm64 + x64
bun run dist:win      # NSIS installer, x64
```

AppImage is always produced on Linux builds — this is a release
invariant. All artifacts are **UNSIGNED** in this repo; do not
distribute them as official releases until signing and notarisation
are set up.

## CI

Every push to `main` and every PR touching `ui/**` or the .NET worker
runs `.github/workflows/ui-ci.yml` on Ubuntu, macOS, and Windows. The
job lints, typechecks, tests, publishes the worker binary, and packages
unsigned artifacts. Run output and artifacts are available from the
workflow run page.

## Troubleshooting

- **`SingletonLock` stuck from a previous crash.** The app self-recovers
  from a dead-PID lock on startup. If it persists, remove
  `~/.config/@scout/desktop/SingletonLock*` manually and relaunch.
- **"Preload not found" warning at startup.** Build the preload first:
  `bun run --cwd packages/preload build`. The renderer and main builds
  should follow.
- **Discord: `Invalid OAuth2 redirect_uri`.** Register
  `http://127.0.0.1:53682/callback` as a redirect URI in your
  application's Discord Developer Portal.
- **Bot does not respond to `@scout ping`.** Ensure the bot role has
  `SEND_MESSAGES` and that the `MESSAGE_CONTENT_INTENT` is enabled in
  the Discord Developer Portal.

## Where docs live

Architecture, ADRs, plans, and the design system live in the Scout
project vault at `obsidian-vault/scout/`.
