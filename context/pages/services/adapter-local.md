---
title: "Adapter.Local"
version: "1.16"
date: 2026-10-30
updated: 2026-11-11
changes: "v1.16 (2026-11-11) a live translation the AI Source answers HTTP 404 for its model says the model is not installed, by name, with the conversation's missing-model sentence."
page_type: service
status: active
description: "Local chat adapter for the desktop's quick-ask window: reads one JSON request per line from the worker's stdin and streams the reply as SCOUT_LOCAL lines on stdout, as the trusted local owner."
source: ["src/Scout.Adapter.Local/", "tests/Scout.Adapter.Local.Tests/", "src/Scout.Host.Console/HostComposition.cs"]
reliability: high
---

# Adapter.Local

> Lets the owner ask Scout from the desktop's quick-ask window, typed or spoken, through the worker's own stdin and stdout.

## Responsibility

Owns reading requests from stdin, turning each into a local-owner `ChatMessage`, cancelling a request, running up to two live transcription sessions at once, answering a read with the text on a screenshot, turning the four meeting requests into calls on the meetings store, answering a speak with the audio of one sentence, and writing reply events to stdout. Does not own routing, memory, transcription or the access rules ([[pages/services/conversation]]), nor the meetings database or the Markdown document ([[pages/services/meetings]]), nor the quick-ask window, the shortcut or the microphone ([[pages/services/desktop-quick-ask]]).

## Rules

The rules this project must keep, and what to read before changing it, are in `.claude/rules/adapter-local.md`. Claude Code loads that file when a task touches the project.

## Topics

Read the page your change touches, not all of them.

| Page | Read when a change touches | Characters |
|---|---|---|
| [[pages/services/adapter-local/public-contract\|Public contract]] | Public contract | 7449 |
| [[pages/services/adapter-local/how-it-works-startasync-opens-stdin-and-starts-a\|How it works: StartAsync opens stdin and starts a]] | StartAsync opens stdin and starts a; DispatchAsync (src/Scout.Adapter.Local/LocalChatAdapter.cs:295) handles each line, and; An oversized line gets fail That; Malformed JSON, or a request with; cancel cancels the... | 12528 |
| [[pages/services/adapter-local/how-it-works-one-translation-runs-at-a-time\|How it works: One translation runs at a time]] | One translation runs at a time; Context is the two finals spoken; At most 20 translations start in; Each call has 20 s (src/Scout.Adapter.Local/LiveTranslationQueue.cs:21).; Without an ILiveTranslator the listen is;... | 11425 |
| [[pages/services/adapter-local/hot-spots\|Hot spots]] | Hot spots | 691 |
| [[pages/services/adapter-local/testing-footprint\|Testing footprint]] | Testing footprint | 8319 |

## Related

Direct project references: [[pages/services/conversation]]. Referenced by [[pages/services/host-console]]. Map: [[pages/architecture/project-map]]. Decisions: [[pages/decisions/0028-local-quick-ask-assistant]], [[pages/decisions/0034-live-transcription-on-the-local-adapter]], [[pages/decisions/0035-screenshot-attached-to-a-question]], [[pages/decisions/0036-text-read-from-a-screenshot-by-a-local-ocr-program]], [[pages/decisions/0037-meetings-kept-locally-in-sqlite]]. The store: [[pages/services/meetings]].
