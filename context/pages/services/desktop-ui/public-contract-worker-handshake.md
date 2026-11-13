---
title: "Desktop UI: Public contract: Worker handshake"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Desktop UI service page."
page_type: service
status: active
description: "Read when a change touches: Worker handshake; Routes."
source: - ui/packages/main/src - ui/packages/preload/src - ui/packages/renderer/src - ui/packages/shared/src - ui/build
reliability: high
---

## Public contract

- **Worker handshake** - the main process spawns the worker binary (`ui/packages/main/src/worker/supervisor.ts:240`) and waits for the `SCOUT_READY` marker on stdout (`ui/packages/main/src/worker/log-stream.ts:15`); readiness times out after 45 s. A `SCOUT_PAIRED=<userId>` or `SCOUT_PAIRING_BURNED` line on stdout, never stderr, goes to the pairing handlers (`:37`, `:39`, see Allowed users). A `SCOUT_LOCAL ` line on stdout goes to the quick-ask router only and never into the log ring or the log viewer (`ui/packages/main/src/worker/log-stream.ts:24`); every other line is handed to the supervisor once, which keeps it in its ring and forwards it; main writes quick-ask questions to the worker's stdin through `writeStdin` (`ui/packages/main/src/worker/supervisor.ts:390`), see Quick ask.

- **Routes** - `''`, `provision`, `token`, `oauth`, `launch`, `ready`, `status`, `sources`, `sources/local-models`, `features`, `review`, `meetings`, `llm`, `logs`, `slack`, `settings`, `chat`, `quick-ask`, `operator` and `floating-button` in `ui/packages/renderer/src/app/app.routes.ts:10`, `meetings` at `:55`; `status` only redirects to `logs` (`:40`), `sources/local-models` to `/llm?tab=transcription` (`:45`), and `logs` redirects to `settings` while Logs is hidden from the menu (`:64`); `operator` (`:86`) holds `production`, where it opens, `assembly` (`:97`) and `approvals`, whose route data `feature: 'youtube'` filters the Review screen (`:117`); there is no `production` route in the main window, so `/production` falls to the wildcard. `quick-ask` (`:82`), `operator` and `floating-button` (`:123`) render without the wizard shell (`ui/packages/renderer/src/app/app.component.ts:22`).
