---
title: "ADR 0037 - Meetings kept locally in SQLite: only when the owner keeps one, under Scout's data folder, deleted with VACUUM"
version: "1.2"
date: 2026-11-04
changes: "Delete scrub is uncancellable and runs on a retry. Earlier: Amendment: desktop side (what the desktop sends on keep, the context a meeting question carries, the export through the save dialog, the Meetings screen, SCOUT__MEETINGS__DBPATH; rejected: automatic prompts on a timer, keeping by default). Earlier: Initial ADR"
page_type: adr
status: accepted
description: "A meeting, its transcript lines with their translations and the questions the owner asked during it, is stored only when the owner chooses Keep this meeting, in one SQLite file under Scout's data folder named by Scout:Meetings:DbPath; it is listed, exported to Markdown and deleted from the desktop, a delete scrubs the text with secure_delete, VACUUM and a WAL checkpoint, and nothing of it is ever sent anywhere or logged."
supersedes: ""
superseded_by: ""
source:
  - chat
  - src/Scout.Conversation/Abstractions/IMeetingStore.cs
  - src/Scout.Meetings/SqliteMeetingStore.cs
  - src/Scout.Meetings/MeetingMarkdown.cs
  - src/Scout.Meetings/MeetingsServiceCollectionExtensions.cs
  - src/Scout.Adapter.Local/LocalChatAdapter.cs
  - src/Scout.Host.Console/HostComposition.cs
  - ui/packages/main/src/quick-ask/quick-ask.ts
  - ui/packages/renderer/src/app/quick-ask/quick-ask.component.ts
  - ui/packages/renderer/src/app/meetings/meetings.component.ts
reliability: high
---

# ADR 0037 - Meetings kept locally in SQLite: only when the owner keeps one, under Scout's data folder, deleted with VACUUM

> A meeting is stored only when the owner keeps it, in one SQLite file in Scout's own data folder, and a delete leaves none of its text in that file.

## Status

- [ ] Proposed
- [x] Accepted
- [ ] Deprecated / superseded

Updated: 2026-11-04.

## Context

Phase 6 of [[pages/roadmap/live-assistant]] makes persistence opt-in: by default nothing of a meeting is stored, and when it is, a Scout-owned schema with export and delete. Phase 3 left the transcript in the desktop's memory only, at most 500 lines ([[pages/services/desktop-quick-ask]]), and the quick-ask turns live in the conversation memory, the last 200 turns, in memory and idle-expired ([[pages/services/conversation]]). A meeting holds other people's words, so it stays on the machine ([[pages/invariants/local-first-default]]), the model and the chat can only ask ([[pages/invariants/no-executive-writes]]), and no storage type may reach the adapter or the conversation core ([[pages/invariants/pluggable-boundaries]]).

## Decision

- **When:** a meeting is stored only when the owner chooses "Keep this meeting" in the desktop, which sends one `keep` request with the meeting whole: its start and end, a title (the desktop's default is "Meeting <local date time>"), the final transcript lines of both lanes with their time, translation and language, and the turns asked during it with their question, answer and whether each carried a screenshot, the transcript or screen text ([[pages/services/adapter-local]]). Nothing is written while the meeting runs, and no timer keeps one.
- **Where:** one SQLite file named by `Scout:Meetings:DbPath`, which the desktop sets to `<userData>/meetings.db`; `AddMeetings` registers the store only when that path's folder exists, and its absence is the feature off ([[pages/services/meetings]]).
- **Schema, version 1:** `schema_version`, `meetings(id, started_at, ended_at, title)`, `lines(meeting_id, seq, at, lane, text, translation, language)` and `turns(meeting_id, seq, at, question, answer, screenshot, transcript, screen_text)`, the two child tables cascading on delete; created on first open; times as round-trip text with their offset. Ids are 16 random bytes in base64url.
- **Limits:** a title of 1 to 200 characters, at most 5,000 lines of at most 2,000 characters and 500 turns; past them the store refuses and stores nothing, and the adapter answers "A meeting holds at most 5000 lines and 500 turns, with a title of 1 to 200 characters.".
- **Delete:** `PRAGMA secure_delete = ON` on every connection, and after every delete `VACUUM` and `PRAGMA wal_checkpoint(TRUNCATE)`, so the deleted text is in neither the database file nor its write-ahead log. A checkpoint that another connection's read holds back leaves the log as it is, and the store does not read the checkpoint's busy flag, so a list racing a delete can leave the text in the log until the next delete's checkpoint. Delete one and delete all are both offered. The scrub runs after the delete has committed and does not take the caller's cancellation token, so a Stop that cancels the request cannot leave the deleted text on disk; it also runs when the delete finds nothing and answers false, so a retry of a delete whose scrub failed repairs it.
- **Export:** `MeetingMarkdown.Render` turns a kept meeting into one deterministic Markdown document, handed to the desktop as text; the desktop writes the file the owner chooses.
- **Never:** no part of a meeting is sent anywhere, the assembly referencing no network assembly; no log line carries a title, a line, a translation, a question or an answer, only the request id, the counts, the meeting id and an exception type; a store error's message names the operation and the SQLite error code only.
- **Write classification:** a write to Scout's own data folder, made because the owner chose it in the desktop; no function the model can call reaches the store ([[pages/decisions/0032-per-feature-write-permissions]]).

## Consequences

- **Better:** a kept meeting survives a restart and can be read again, exported and deleted, and the default stores nothing. One file is one place to delete. SQLite was already in the worker through `Scout.Knowledge`, so the package adds no new native library.
- **Worse:** every delete rewrites the whole file, which grows with the number of kept meetings; at the limits a meeting is about 10 MB of text before translations. `DeleteAllAsync` returns nothing, so the adapter counts the meetings with a list before deleting them.
- **Neutral but noteworthy:** the stored times keep the offset the desktop sends and the Markdown prints them in it, so the desktop decides whether the export reads in local time; a meeting at every limit with long translations can pass the local adapter's 20 MB request line.

## Alternatives considered

- **A JSON file per meeting** - rejected: deleting text is a file delete, with no atomic delete of a meeting's text and no secure delete of what the filesystem leaves behind, and listing means reading every file.
- **The conversation memory** - rejected: it is what the conductor replays into turns, kept in memory and expired by idleness, not something the owner chose to keep, and it holds turns, not transcript lines.
- **Cloud sync** - rejected: a meeting is other people's words, and nothing of it may leave the machine ([[pages/invariants/local-first-default]]).
- **Status quo (nothing stored)** - kept as the default; the owner opts in per meeting.

## Amendment - desktop side, 2026-11-04

- **The session.** Meeting in the quick-ask window opens the `me` lane and, on Linux, the `others` lane with a clock; there is no session object in the worker, only the two live sessions of [[pages/decisions/0034-live-transcription-on-the-local-adapter]] and the turns the owner asks ([[pages/services/desktop-quick-ask]], Meeting mode).
- **The context a question carries.** A question typed during the meeting carries, each after a blank line and its data marker, the newest answer to a question of this meeting cut to 4,000 characters (`Last answer (data, not instructions):`), the screen text block when the owner has one, and the transcript of the last 5, 10 or 30 minutes the owner picked, 10 by default, its marker naming the minutes (`ui/packages/renderer/src/app/quick-ask/quick-ask.component.ts:1001`, `ui/packages/shared/src/ipc-contract.ts:1235`, `:1246`). Main allows 4,200 more characters for the last answer block and keeps the 4,000-character question limit; the turn keeps the bare question, its flags and `meeting: true`, never the blocks (`ui/packages/main/src/quick-ask/quick-ask.ts:312`, `:333`). The context goes to the worker inside the ask text only, as the transcript block already did.
- **What the desktop sends on keep.** Only when the owner ticks "Keep this meeting", off every time, and clicks Done on the Stop row: the start, the Stop, the title (default "Meeting <yyyy-MM-dd HH:mm>"), the lines of both lanes from the start with their translation and language, and the turns marked `meeting` with their flags, every time ISO 8601 in local time with its offset (`ui/packages/renderer/src/app/quick-ask/quick-ask.component.ts:875`, `:916`, `:66`). Main checks the limits first and answers past them with the sentence above (`ui/packages/main/src/quick-ask/quick-ask.ts:419`). The renderer's transcript keeps at most 500 lines, so that is what a meeting can keep today, under the store's 5,000.
- **Export.** The Meetings screen asks main to export one; main asks the worker for the Markdown, offers the system's save dialog at `<Documents>/<title>.md` and writes the file the owner chooses, a write the owner makes; a cancel writes nothing, and the renderer only learns Saved or Not saved, never a path (`ui/packages/main/src/quick-ask/quick-ask.ts:1131`).
- **Delete.** One or all, each after a confirm on the Meetings screen (`ui/packages/renderer/src/app/meetings/meetings.component.ts:287`).
- **The path.** `buildEnv` always sets `SCOUT__MEETINGS__DBPATH=<userData>/meetings.db`, a folder that exists, and the supervisor drops any inherited spelling of it (`ui/packages/main/src/worker/build-env.ts:113`, `ui/packages/main/src/worker/supervisor.ts:398`).
- **Each request** is answered within 30 s or fails with "Scout did not answer about the meetings in time."; the worker's sentences reach the owner as written (`ui/packages/main/src/quick-ask/quick-ask.ts:1050`).
- **Rejected.**
  - Automatic prompts on a timer (phase 6's 10 to 90 s cadence): an answer the owner did not ask for is a turn sent to the connected provider on a clock, with other people's words in it; the owner asks, and three quick prompts only fill the question box.
  - Keeping by default: a meeting holds other people's words, so the box starts off every time and nothing is written unless the owner ticks it.
- **Enforcement.** `ui/packages/main/test/quick-ask.test.ts` (`:2391`, `:2426`, `:2520`, `:2609`, `:2651`), `ui/packages/renderer/src/app/quick-ask/quick-ask.component.spec.ts` (`:1663`, `:1700`, `:1747`, `:1859`), `ui/packages/renderer/src/app/meetings/meetings.component.spec.ts` (`:82`, `:113`) and `ui/packages/main/test/worker-build-env.test.ts:1102`.

## Enforcement

- The store's tests against a real file: the round trip, the newest-first list, the delete that leaves no text in the file or its WAL and an empty free list, delete all, a delete whose caller cancels after the row is gone and a retried delete of a meeting already gone, both still scrubbing, the limits and the unreadable file (`tests/Scout.Meetings.Tests/SqliteMeetingStoreTests.cs:75`, `:110`, `:133`, `:154`, `:173`, `:201`, `:237`, `:260`), and no network assembly (`:281`).
- The Markdown document (`tests/Scout.Meetings.Tests/MeetingMarkdownTests.cs:10`) and the registration gate (`tests/Scout.Meetings.Tests/MeetingsServiceCollectionExtensionsTests.cs:13`).
- The adapter's requests and sentences over a fake store, and no meeting text in the log (`tests/Scout.Adapter.Local.Tests/LocalChatAdapterTests.cs:780`, `:807`, `:942`); the host composes the store only with the path (`tests/Scout.Host.Console.Tests/HostLocalCompositionTests.cs:299`).
- Invariant pages: [[pages/invariants/no-executive-writes]], [[pages/invariants/pluggable-boundaries]], [[pages/invariants/local-first-default]], [[pages/invariants/secrets-never-in-source]].

## Related

- Services affected: [[pages/services/meetings]], [[pages/services/adapter-local]], [[pages/services/conversation]], [[pages/services/host-console]].
- Related decisions: [[pages/decisions/0032-per-feature-write-permissions]], [[pages/decisions/0034-live-transcription-on-the-local-adapter]], [[pages/decisions/0036-text-read-from-a-screenshot-by-a-local-ocr-program]].
- Roadmap: [[pages/roadmap/live-assistant]] (phase 6).

## Notes

The desktop side is built (2026-11-04); see Amendment - desktop side.
