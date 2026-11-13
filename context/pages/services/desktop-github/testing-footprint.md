---
title: "Desktop GitHub card: Testing footprint"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Desktop GitHub card service page."
page_type: service
status: active
description: "Read when a change touches: Testing footprint."
source: - ui/packages/main/src/oauth/github-auth.ts - ui/packages/main/src/oauth/github-bot.ts - ui/packages/main/src/ipc/github.ts - ui/packages/main/src/ipc/github-verdict.ts - ui/packages/main/src/notifications/github-activity.ts - ui/packages/renderer/src/app/features/github-connect.component.ts - pages/services/desktop-ui
reliability: high
---

## Testing footprint

`ui/packages/main/test/oauth-github.test.ts` (with the count of the App's repositories), `oauth-github-token.test.ts`, `oauth-github-bot.test.ts`, `ipc-github.test.ts`, `ipc-github-verdict.test.ts` (the click sent once, the 2-second follow pushing each change once and stopping when given or failed, an unreachable control plane, five failed reads, eight malformed requests refused before any call, the status channel, shutdown, one action per pull request and commit across clicks and windows (`ui/packages/main/test/ipc-github-verdict.test.ts:293`, `:330`), the lookup for a later window (`:350`) and the push to every open window), and `ui/packages/renderer/src/app/features/github-connect.component.spec.ts`.
