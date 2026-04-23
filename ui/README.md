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

| Package                   | Description                              |
| ------------------------- | ---------------------------------------- |
| `@scout/shared`           | Shared TypeScript types and IPC contract |
| `@scout/desktop-main`     | Electron main process                    |
| `@scout/desktop-preload`  | Electron preload script (contextBridge)  |
| `@scout/desktop-renderer` | Angular 21 renderer (added in PR 5)      |
| `@scout/desktop-e2e`      | Playwright E2E tests (added in PR 13)    |

## Commands

```sh
bun install          # install all workspace deps
bun run lint         # ESLint across packages/
bun run typecheck    # tsc --noEmit per package
bun run test         # no-op placeholder — real behaviour lands in a later PR
bun run build        # no-op placeholder — real behaviour lands in a later PR
bun run format       # Prettier check (validates formatting; no code generated yet)
bun run format:write # Prettier write
bun run clean        # remove dist/ and node_modules/
```

Bun is the runtime for all workspace commands. Node is only required on the host
for `@electron/rebuild` (native modules); install the version pinned in `.nvmrc`
if you plan to run `bun run rebuild` locally. Contributors who only touch UI code
do not need Node at all.

## Architecture

See the plan document linked above for the full architecture diagram, IPC contract,
PR breakdown, and security posture.
