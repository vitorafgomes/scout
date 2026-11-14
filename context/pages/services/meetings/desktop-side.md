---
title: "Meetings: Desktop side"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Meetings service page."
page_type: service
status: active
description: "Read when a change touches: Desktop side."
source: ["src/Scout.Meetings/", "tests/Scout.Meetings.Tests/", "src/Scout.Conversation/Abstractions/IMeetingStore.cs", "Directory.Packages.props", "tests/Scout.ArchTests/PersistenceTests.cs", "context/pages/decisions/0052-persistence-behind-stores-on-entity-framework.md", "ui/packages/main/src/quick-ask/quick-ask.ts", "ui/packages/renderer/src/app/meetings/meetings.component.ts"]
reliability: high
---

## Desktop side

What the desktop sends and shows; the detail is on [[pages/services/desktop-quick-ask]] (Meeting mode) and [[pages/services/desktop-ui]] (Meetings).

- **Keep.** The quick-ask window's Stop row sends one `keep` only when the owner ticks "Keep this meeting", off by default, and clicks Done (`doneMeeting`, `ui/packages/renderer/src/app/quick-ask/quick-ask.component.ts:1776`). Done first waits, at most 20 s a lane, for both lanes to end (`:1785`), so the last final and its translation the worker sends after Stop are in. The meeting carries the lines of the meeting's own sessions from its start, with their translation and language, so a Listen opened on the Stop row stays out (`meetingRecord`, `:1818`, `:1829`), and the turns asked during it with their flags, every time in local time with its offset. Main checks the limits above before the worker sees the meeting and answers past them with the adapter's own sentence (`toMeeting`, `ui/packages/main/src/quick-ask/quick-ask.ts`). It writes the keep with a `keepKey`, the base64url SHA-256 of the meeting's `startedAt`, fixed for the whole meeting, so a retry of the same meeting carries the same key even after its title, turns or translations changed (`keepMeeting`, `ui/packages/main/src/quick-ask/quick-ask.ts:1676`, `:1682`), and once the worker answers `kept` it pushes `scout:local.meetingsChanged` to every window (`:1686`).
- **List, export, delete.** The Meetings screen asks for at most 100 meetings, newest first, and reads them again on every `scout:local.meetingsChanged` (`ui/packages/renderer/src/app/meetings/meetings.component.ts:333`). It exports one through the system's save dialog, which asks before it overwrites a file (`showOverwriteConfirmation`, `ui/packages/main/src/quick-ask/quick-ask.ts:1710`), and main writes the `markdown` text to the path picked there. It deletes one or all after a confirm; Delete all says "This removes every kept meeting", since the list holds at most 100 (`ui/packages/renderer/src/app/meetings/meetings.component.ts:367`). Each delete, answered or refused, reads the list again, so a meeting deleted elsewhere leaves its row (`:382`, `:385`), and main pushes `scout:local.meetingsChanged` after a delete (`ui/packages/main/src/quick-ask/quick-ask.ts:1739`). Each request carries an `m-<n>` id and a 30 s guard, and the adapter's `fail` sentences, such as the not-configured one, reach the owner as written (`ui/packages/main/src/quick-ask/quick-ask.ts:1641`).
- **Never.** The renderer never sends or receives a path, and main logs only ids, counts and the export's length.
