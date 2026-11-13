---
title: "Desktop GitHub card: Hot spots"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Desktop GitHub card service page."
page_type: service
status: active
description: "Read when a change touches: Hot spots."
source: - ui/packages/main/src/oauth/github-auth.ts - ui/packages/main/src/oauth/github-bot.ts - ui/packages/main/src/ipc/github.ts - ui/packages/main/src/ipc/github-verdict.ts - ui/packages/main/src/notifications/github-activity.ts - ui/packages/renderer/src/app/features/github-connect.component.ts - pages/services/desktop-ui
reliability: high
---

## Hot spots

- `ui/packages/main/src/oauth/github-bot.ts:103` - the bot's custody and token refresh.
- `ui/packages/main/src/ipc/github-verdict.ts:136` - one action per key and following it.
