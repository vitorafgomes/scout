---
title: "Meetings: Testing footprint"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Meetings service page."
page_type: service
status: active
description: "Read when a change touches: Testing footprint."
source: ["src/Scout.Meetings/", "tests/Scout.Meetings.Tests/", "src/Scout.Conversation/Abstractions/IMeetingStore.cs", "Directory.Packages.props", "tests/Scout.ArchTests/PersistenceTests.cs", "context/pages/decisions/0052-persistence-behind-stores-on-entity-framework.md", "ui/packages/main/src/quick-ask/quick-ask.ts", "ui/packages/renderer/src/app/meetings/meetings.component.ts"]
reliability: high
---

## Testing footprint

`tests/Scout.Meetings.Tests` (25 test attributes, 32 cases, 3 files on 2026-11-11), against a real file in a temporary folder:
- A kept meeting read back with both lanes, the translation and its language, the turns and their flags, and the offset (`tests/Scout.Meetings.Tests/SqliteMeetingStoreTests.cs:77`); two keeps with distinct 22-character ids, one schema row at version 1 and WAL mode (`:97`); a keep retried under the same key by a new store over the same file, as after a worker restart, answering the first id with one meeting stored (`:112`), and keeps under different keys each stored (`:125`); a delete and a delete all of keyed meetings answering true, leaving no key, and a new keep under a released key getting a new id (`:137`, `:152`).
- The list newest first across three offsets, with its counts and its limit (`tests/Scout.Meetings.Tests/SqliteMeetingStoreTests.cs:168`).
- A delete taking the lines and turns, leaving the marker text in neither the file nor its WAL, `freelist_count` 0, the other meeting intact, and a second delete false (`tests/Scout.Meetings.Tests/SqliteMeetingStoreTests.cs:191`); delete all (`:212`), which answers how many meetings it deleted and not their lines or turns (`:477`); a delete whose caller cancels after the row is gone, for one and for all, still scrubbing (`tests/Scout.Meetings.Tests/SqliteMeetingStoreTests.cs:231`); a retried delete of a meeting already gone still scrubbing (`:259`).
- 5,001 lines, 501 turns, a blank title, a 201-character title and a 2,001-character line refused with nothing stored (`tests/Scout.Meetings.Tests/SqliteMeetingStoreTests.cs:295`), and a meeting at every limit kept (`:305`).
- A file that is not a database throwing `MeetingStoreException` with error 26 and no meeting text, on keep and on list (`tests/Scout.Meetings.Tests/SqliteMeetingStoreTests.cs:318`), the Rule I test of this boundary, and a keyed keep against the same file throwing error 26 with neither the meeting text nor the key (`:339`); a keep on a read-only file throwing error 8 from inside `SaveChanges`, without the text or the key, and storing nothing (`:353`), the Rule I test of the Entity Framework path; a file an older build wrote created again with the current schema (`:370`); an older build's file in a folder Scout cannot write to failing with `UnauthorizedAccessException` and still there (`:422`); an older build's file holding a foreign table never deleted, the call saying why (`:440`).
- No network assembly referenced (`tests/Scout.Meetings.Tests/SqliteMeetingStoreTests.cs:466`).
- The whole Markdown document of a meeting with a translation, a multi-line answer and each flag combination, twice identical (`tests/Scout.Meetings.Tests/MeetingMarkdownTests.cs:10`), and the empty sections (`:68`).
- `AddMeetings` with no path, a missing folder and an existing one, the renderer present exactly with the store and no file created (`tests/Scout.Meetings.Tests/MeetingsServiceCollectionExtensionsTests.cs:13`).
