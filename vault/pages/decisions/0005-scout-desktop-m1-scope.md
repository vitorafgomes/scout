---
title: "Scout Desktop M1 scope and deferrals"
version: "1.0"
date: 2026-04-24
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: adr
status: accepted
description: "Plan 5 bootstrapped Scout Desktop end-to-end. This ADR captures the technical decisions made during M1 and the explicit deferrals, so post-M1 work can build on the same foundation without rediscovery."
source:
  - "obsidian-vault/scout/06-decisions/2026-04-24-scout-desktop-m1-scope.md"
reliability: medium
---

# Scout Desktop M1 scope and deferrals

## Context

Plan 5 bootstrapped Scout Desktop end-to-end. This ADR captures the
technical decisions made during M1 and the explicit deferrals, so
post-M1 work can build on the same foundation without rediscovery.

## Decisions

- Bun over npm/yarn/pnpm for the workspace toolchain. Tradeoff: fewer
  eyes on edge-case lockfile behaviour in exchange for install speed
  and a single binary running every workspace script.
- Angular 21 with zoneless change detection, Signals, standalone
  components, and the new `@if` / `@for` control flow. No NgModules,
  no zone.js, no RxJS in the renderer. Tradeoff: smaller third-party
  library surface that assumes NgModules, in exchange for a cleaner
  mental model and smaller bundle.
- Electron main in ESM (`"type": "module"`); preload in CommonJS
  because sandboxed preload cannot be ESM. Tradeoff: two module
  systems in the repo in exchange for modern main + sandboxed preload.
- CSP strict in production; relaxed only under `NODE_ENV=development`
  for the Angular dev server. Tradeoff: an extra code path to review
  in exchange for HMR during development.
- `safeStorage` for the bot token at rest; `localStorage` for
  non-secret renderer state (clientId, guildId, guildName). Tradeoff:
  no cross-device sync of provisioning state in exchange for no
  secrets touching disk in cleartext.
- Full `ScoutBridge` IPC surface typed in `@scout/shared`; no generic
  `ipcRenderer` exposure to the renderer. Tradeoff: every new channel
  requires a contract update in exchange for a compromised renderer
  being unable to call unknown handlers.
- OAuth2 loopback on strict `127.0.0.1:53682` with `findFreePort`
  fallback up to `53690`; PKCE S256 verifier included even though
  Discord bot install does not require it. Tradeoff: one extra field
  on the wire in exchange for a PKCE-ready flow when we add Slack or
  other IdPs.
- `.NET` worker shipped inside the Electron bundle via
  `electron-builder` `extraResources`; single-file self-contained
  publish per RID. Tradeoff: ~60-80 MB per RID in exchange for
  zero-prerequisite install on the end-user's machine.
- Supervisor exponential-backoff restart (1/2/4/8/16s, 5 failures in
  60s → `crashed`); 30s readiness timeout keyed on the `SCOUT_READY`
  stdout marker; SIGTERM → 5s → SIGKILL on stop. Tradeoff: a crashed
  state surfaced to the user in exchange for never looping a broken
  worker forever.
- Help plugin registered unconditionally as the baseline responder
  (`ping`, `help`, `version`). Tradeoff: one always-loaded plugin in
  exchange for the bot being observable end-to-end with no external
  config.
- `electron-builder` for packaging; `electron-forge` considered and
  rejected. Tradeoff: slightly more verbose YAML in exchange for more
  mature cross-platform matrix and better `extraResources` ergonomics.
- Unsigned artifacts across all three platforms; AppImage is the
  Linux release invariant. Tradeoff: Gatekeeper / SmartScreen warnings
  on end-user machines in exchange for M1 shipping without a signing
  pipeline.
- 3-OS CI matrix on pushes to `main` and PRs touching `ui/**` or the
  worker. Tradeoff: longer CI minutes in exchange for catching
  platform-specific regressions at PR time.
- `SingletonLock` stale-PID recovery with strict same-host validation
  and a positive-integer parser. Tradeoff: extra startup code in
  exchange for the app surviving a previous crash without manual
  cleanup.

## Deferrals

- Code signing / notarization / `electron-updater` — deferred to
  post-M1. The cert and Apple Developer ID setup is non-trivial;
  unsigned is enough for contributor testing.
- Slack wizard via `apps.manifest.create` — deferred to a follow-up
  milestone.
- Teams detection/wizard — detection only; no provisioning, because
  sideloading is policy-blocked in most tenants.
- Internationalisation, telemetry, and multi-bot management — post-M1
  expansion.
- `scout init` CLI convergence — will share `packages/shared` when the
  CLI is built.
- Discord adapter ID-redaction for logs — called out in review;
  accepted as Info level for operator triage. Revisit if a remote log
  sink is added.
- Observability: OpenTelemetry in the renderer and main — not in M1
  scope.

## Consequences

The project now has a reproducible local build story (`bun install &&
bun run dist:linux`), a CI matrix that fails loudly on regressions,
and a Help plugin that makes the bot observable end-to-end without
external config. Security posture is solid for a read-only bot: no
secret leaves the main process, no IPC channel exposes an external
write. The main outstanding risks are unsigned distribution
(Gatekeeper and SmartScreen warnings on end-user machines) and the
absence of an auto-update channel — both block taking Scout Desktop
beyond internal testing. Those are the primary inputs to the post-M1
roadmap.

## Links

- Plan 5 (private note)
- [[pages/architecture/electron-provisioning-ui|Feasibility study]]
- [[pages/architecture/desktop-ui-design-system|Design system]]
- [[pages/decisions/0004-web-stack-angular|Web stack ADR]]
