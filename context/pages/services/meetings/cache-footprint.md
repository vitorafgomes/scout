---
title: "Meetings: Cache footprint"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Meetings service page."
page_type: service
status: active
description: "Read when a change touches: Cache footprint."
source: ["src/Scout.Meetings/", "tests/Scout.Meetings.Tests/", "src/Scout.Conversation/Abstractions/IMeetingStore.cs", "Directory.Packages.props", "tests/Scout.ArchTests/PersistenceTests.cs", "context/pages/decisions/0052-persistence-behind-stores-on-entity-framework.md", "ui/packages/main/src/quick-ask/quick-ask.ts", "ui/packages/renderer/src/app/meetings/meetings.component.ts"]
reliability: high
---

## Cache footprint

None. Not applicable: every call opens the file and reads it.
