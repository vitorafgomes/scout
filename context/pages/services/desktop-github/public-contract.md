---
title: "Desktop GitHub card: Public contract"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Desktop GitHub card service page."
page_type: service
status: active
description: "Read when a change touches: Public contract."
source: - ui/packages/main/src/oauth/github-auth.ts - ui/packages/main/src/oauth/github-bot.ts - ui/packages/main/src/ipc/github.ts - ui/packages/main/src/ipc/github-verdict.ts - ui/packages/main/src/notifications/github-activity.ts - ui/packages/renderer/src/app/features/github-connect.component.ts - pages/services/desktop-ui
reliability: high
---

## Public contract

- **Channels:** `scout:github.signIn`, `status`, `cancel`, `signOut`, `saveToken`; `scout:github.bot.status`, `create`, `cancel`, `remove`, `setWrites`; `scout:github.giveVerdict`, `scout:github.verdictStatus`, `scout:github.verdictFor`, and the push `scout:github.verdict`.
- **Worker variables:** `SCOUT__PLUGINS__GITHUB__TOKENFILE` or `SCOUT__PLUGINS__GITHUB__TOKEN`, and with a bot token `SCOUT__PLUGINS__GITHUB__BOTTOKENFILE`, `BOTWRITES`, `BOTLOGIN` and `BOTREPOS` (comma-separated `owner/repo` names the installation covers; `ui/packages/main/src/worker/build-env.ts:88`).
- **External boundaries:** github.com's device flow and api.github.com from main only, every one of `GitHubAuth`'s calls there a GET (since 2026-11-11 also one repository count per installation of the App); the control plane over gRPC for the verdict.
