---
title: "Meetings: Hot spots"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Meetings service page."
page_type: service
status: active
description: "Read when a change touches: Hot spots."
source: ["src/Scout.Meetings/", "tests/Scout.Meetings.Tests/", "src/Scout.Conversation/Abstractions/IMeetingStore.cs", "Directory.Packages.props", "tests/Scout.ArchTests/PersistenceTests.cs", "context/pages/decisions/0052-persistence-behind-stores-on-entity-framework.md", "ui/packages/main/src/quick-ask/quick-ask.ts", "ui/packages/renderer/src/app/meetings/meetings.component.ts"]
reliability: high
---

## Hot spots

- `src/Scout.Meetings/SqliteMeetingStore.cs:283`: the scrub after a delete is what keeps deleted text off the disk; removing either statement fails the delete tests.
- `src/Scout.Meetings/SqliteMeetingStore.cs:56`: the keep-key lookup is what makes a retried keep store nothing; without it the retry stores a second copy.
- `src/Scout.Meetings/SqliteMeetingStore.cs:195`: the limits are checked here only; the adapter maps the `ArgumentException` to its limits sentence ([[pages/services/adapter-local]]).
- `src/Scout.Meetings/SqliteMeetingStore.cs:259`: the older-build check deletes the file; it runs only when the file has tables and no migration history.
