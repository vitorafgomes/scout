---
title: "Desktop quick ask"
version: "1.45"
date: 2026-11-02
updated: 2026-11-12
changes: "v1.45 (2026-11-12): a review that found nothing still shows its card: the tests chips, the target line, \\\"No findings.\\\" and, under it, what Scout says it read, and Approve; no finding card, Post, preview or Request changes.."
page_type: service
description: "The desktop's quick-ask surface: the floating button, the quick-ask window anchored to it, the global shortcut, voice turns, live listening to the microphone and the system audio with a transcript panel and its translation, screenshots and the text read from them, the meeting mode and the meeting the owner may keep, and the conversation shared with the Chat screen."
source:
  - "context/pages/services/desktop-ui.md"
reliability: high
---

# Desktop quick ask

## Rules

The rules this project must keep, and what to read before changing it, are in `.claude/rules/desktop-quick-ask.md`. Claude Code loads that file when a task touches the project.

## Topics

Read the page your change touches, not all of them.

| Page | Read when a change touches | Characters |
|---|---|---|
| [[pages/services/desktop-quick-ask/overview-the-part-of-pages-services-desktop-ui-t\|Overview: > The part of pages/services/desktop-ui that]] | > The part of pages/services/desktop-ui that; The owner's way to ask Scout; Shortcut | 7192 |
| [[pages/services/desktop-quick-ask/overview-window\|Overview: Window]] | Window; Channels | 8955 |
| [[pages/services/desktop-quick-ask/overview-floating-button\|Overview: Floating button]] | Floating button | 17086 |
| [[pages/services/desktop-quick-ask/overview-asking\|Overview: Asking]] | Asking | 15437 |
| [[pages/services/desktop-quick-ask/overview-replies\|Overview: Replies]] | Replies | 29897 |
| [[pages/services/desktop-quick-ask/overview-approval-cards\|Overview: Approval cards]] | Approval cards; Which window asked; Microphone | 11380 |
| [[pages/services/desktop-quick-ask/overview-live-listening\|Overview: Live listening]] | Live listening | 22871 |
| [[pages/services/desktop-quick-ask/overview-screenshot\|Overview: Screenshot]] | Screenshot | 6424 |
| [[pages/services/desktop-quick-ask/overview-text-from-the-screen\|Overview: Text from the screen]] | Text from the screen | 6840 |
| [[pages/services/desktop-quick-ask/overview-meeting-mode\|Overview: Meeting mode]] | Meeting mode | 11975 |
| [[pages/services/desktop-quick-ask/overview-kept-meetings-export-and-delete\|Overview: Kept meetings: export and delete]] | Kept meetings: export and delete; Worker side; No chat platform | 5282 |
| [[pages/services/desktop-quick-ask/read-aloud-and-conversation-mode-pages-decisions\|Read aloud and conversation mode: pages/decisions/0050-read-aloud-and-conversation-mode, built 2026-11-07, in the quick-ask]] | pages/decisions/0050-read-aloud-and-conversation-mode, built 2026-11-07, in the quick-ask; The channel; Read aloud; Conversation mode; Microphone and speaker | 12577 |
| [[pages/services/desktop-quick-ask/read-aloud-and-conversation-mode-drag-region\|Read aloud and conversation mode: Drag region]] | Drag region; Tests: ui/packages/main/test/quick-ask-speech.test.ts (the speak line and | 3521 |
| [[pages/services/desktop-quick-ask/chat-screen\|Chat screen]] | Chat screen | 10578 |
| [[pages/services/desktop-quick-ask/roadmap\|Roadmap]] | Roadmap | 415 |
