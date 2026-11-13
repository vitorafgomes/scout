---
title: "Conversation"
version: "1.27"
date: 2026-10-21
updated: 2026-11-11
changes: "v1.27 (2026-11-11) an LlmModelNotFoundException, a model the AI Source does not have, completes the reply with ModelNotFoundMessage naming the model and pointing at AI Sources, a plain statement and not the generic failure; StreamPump and StreamPumpFailureTests citations re-pointed by content."
page_type: service
status: active
description: "Platform-neutral conversation core: adapter and sink abstractions, the LLM streaming router with short-term memory, and the stream pump that edits a reply as tokens arrive."
source: ["src/Scout.Conversation/", "tests/Scout.Conversation.Tests/"]
reliability: high
---

# Conversation

> Turns one inbound chat message into one streamed reply, without knowing which chat platform or which model is on either end.

## Responsibility

Owns the adapter contract, message and response shapes, routing, per-channel memory, the streaming edit loop and the voice-message flow (limits, download through the adapter's delegate, transcription through `IAudioTranscriber`). Does not own a speech model ([[pages/services/transcription-whisper]]) nor platform SDK calls ([[pages/services/adapter-discord]], [[pages/services/adapter-slack]], [[pages/services/adapter-teams]]) nor model access ([[pages/services/llm]]).

## Rules

The rules this project must keep, and what to read before changing it, are in `.claude/rules/conversation.md`. Claude Code loads that file when a task touches the project.

## Topics

Read the page your change touches, not all of them.

| Page | Read when a change touches | Characters |
|---|---|---|
| [[pages/services/conversation/public-contract\|Public contract]] | Public contract | 9423 |
| [[pages/services/conversation/how-it-works-mermaid\|How it works: mermaid]] | mermaid; Route defers all work to enumeration; Functions per turn; Local owner; Pairing; Allowed users; Progress before the text | 11727 |
| [[pages/services/conversation/how-it-works-blocks-a-function-hands-to-the-repl\|How it works: Blocks a function hands to the reply]] | Blocks a function hands to the reply; Voice messages; The desktop dictates; Voice message without a model, to the desktop; Origin and later posts; Sign-in required; No text from the model; Rejected key | 11545 |
| [[pages/services/conversation/how-it-works-model-not-found\|How it works: Model not found]] | Model not found; Quota or usage limit; A picture on the message; Error endings within the length limit; The overall timeout and the terminal write | 9442 |
| [[pages/services/conversation/hot-spots\|Hot spots]] | Hot spots | 738 |
| [[pages/services/conversation/testing-footprint\|Testing footprint]] | Testing footprint | 5601 |

## Related

Direct project references: [[pages/services/llm]], [[pages/services/skills]]. Referenced by the four adapters, [[pages/services/adapter-local]] among them, and [[pages/services/host-console]]. Map: [[pages/architecture/project-map]]. Dated changes: [[pages/history/conversation]].
