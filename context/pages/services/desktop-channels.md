---
title: "Desktop channels"
version: "1.20"
date: 2026-11-04
updated: 2026-11-12
changes: "A failed update whose single-instance lock another Scout holds leaves the worker stopped instead of starting a second one (2026-11-12).."
page_type: service
status: active
description: "The scout:* IPC channel catalogue of the desktop app grouped by family, with the two channel families that carry their own detail: the pinned Whisper models and the update notice."
source: ["pages/services/desktop-ui.md"]
reliability: high
---

# Desktop channels

> The IPC channel catalogue of [[pages/services/desktop-ui]], and the detail of the whisper and update channel families.

## Rules

The rules this project must keep, and what to read before changing it, are in `.claude/rules/desktop-channels.md`. Claude Code loads that file when a task touches the project.

## Topics

Read the page your change touches, not all of them.

| Page | Read when a change touches | Characters |
|---|---|---|
| [[pages/services/desktop-channels/ipc-channels\|IPC channels]] | IPC channels | 6957 |
| [[pages/services/desktop-channels/whisper-models\|Whisper models]] | Whisper models | 6587 |
| [[pages/services/desktop-channels/update-notice\|Update notice]] | Update notice | 10100 |

## Related

[[pages/services/desktop-ui]] owns the app; [[pages/services/desktop-quick-ask]] the `scout:local.*` turns and meetings; [[pages/services/desktop-operator]] the `scout:operator.*` channels; [[pages/decisions/0031-desktop-self-update]] the update decision.
