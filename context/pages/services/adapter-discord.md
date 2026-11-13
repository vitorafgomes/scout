---
title: "Adapter.Discord"
version: "1.6"
date: 2026-10-21
updated: 2026-11-11
changes: "The log line of a successful later post moves to OriginNotifier, the same for every adapter (2026-11-11). Earlier: a successful later post writes one Information line with the text length, never the text or an id (2026-11-11).."
page_type: service
status: active
description: "Discord chat adapter: receives mentions, direct messages and voice messages over the gateway, opens a thread per conversation in server channels, and streams the reply into embeds that continue across messages."
source: ["src/Scout.Adapter.Discord/", "tests/Scout.Adapter.Discord.Tests/"]
reliability: high
---

# Adapter.Discord

> Connects Scout to Discord with a bot token and shows the answer as it streams.

## Responsibility

Owns the Discord gateway session, message filtering, the thread per conversation, the voice-message projection and download, and the response sink. Does not own routing, memory or transcription ([[pages/services/conversation]], [[pages/services/transcription-whisper]]).

## Rules

The rules this project must keep, and what to read before changing it, are in `.claude/rules/adapter-discord.md`. Claude Code loads that file when a task touches the project.

## Topics

Read the page your change touches, not all of them.

| Page | Read when a change touches | Characters |
|---|---|---|
| [[pages/services/adapter-discord/public-contract\|Public contract]] | Public contract | 2992 |
| [[pages/services/adapter-discord/how-it-works\|How it works]] | How it works | 9538 |
| [[pages/services/adapter-discord/hot-spots\|Hot spots]] | Hot spots | 700 |
| [[pages/services/adapter-discord/testing-footprint\|Testing footprint]] | Testing footprint | 3573 |

## Related

Direct project references: [[pages/services/conversation]]. Referenced by [[pages/services/host-console]]. Map: [[pages/architecture/project-map]]. Dated changes: [[pages/history/adapter-discord]].
