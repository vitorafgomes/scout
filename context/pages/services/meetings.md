---
title: "Meetings"
version: "1.7"
date: 2026-11-04
updated: 2026-11-12
changes: "Entity Framework Core and `Microsoft.Data.Sqlite` on 10.0.15 (2026-11-12). Earlier: Delete all answers how many meetings it deleted, the count of its one DELETE, so the local adapter reports it without listing every meeting; citations past the delete re-pointed by content (2026-11-11).."
page_type: service
status: active
description: "Keeps the meetings the owner chose to keep, their transcript lines and their questions, in one SQLite file under Scout's data folder, renders one as Markdown, and deletes them so their text does not linger; registered only when the meetings database is configured."
source: ["src/Scout.Meetings/", "tests/Scout.Meetings.Tests/", "src/Scout.Conversation/Abstractions/IMeetingStore.cs", "Directory.Packages.props", "tests/Scout.ArchTests/PersistenceTests.cs", "context/pages/decisions/0052-persistence-behind-stores-on-entity-framework.md", "ui/packages/main/src/quick-ask/quick-ask.ts", "ui/packages/renderer/src/app/meetings/meetings.component.ts"]
reliability: high
---

# Meetings

> Keeps a meeting on the owner's machine only when the owner chooses to, and gives it back as Markdown or deletes it.

## Responsibility

Owns the meetings database: its schema and its migrations, keeping a meeting with its lines and turns in one transaction, listing, reading, deleting with the text scrubbed from the file, the limits of a meeting, and the Markdown document of one. Does not own when a meeting is kept, the wire requests or their sentences ([[pages/services/adapter-local]]), nor the Stop row's "Keep this meeting", the Meetings screen or the file the owner saves the export to (the desktop, [[pages/services/desktop-quick-ask]]).

## Rules

The rules this project must keep, and what to read before changing it, are in `.claude/rules/meetings.md`. Claude Code loads that file when a task touches the project.

## Topics

Read the page your change touches, not all of them.

| Page | Read when a change touches | Characters |
|---|---|---|
| [[pages/services/meetings/public-contract\|Public contract]] | Public contract | 2920 |
| [[pages/services/meetings/how-it-works\|How it works]] | How it works | 8004 |
| [[pages/services/meetings/desktop-side\|Desktop side]] | Desktop side | 3221 |
| [[pages/services/meetings/hot-spots\|Hot spots]] | Hot spots | 1284 |
| [[pages/services/meetings/cache-footprint\|Cache footprint]] | Cache footprint | 704 |
| [[pages/services/meetings/cross-service-dependencies\|Cross-service dependencies]] | Cross-service dependencies | 823 |
| [[pages/services/meetings/testing-footprint\|Testing footprint]] | Testing footprint | 3640 |
| [[pages/services/meetings/known-violations-p1-debt\|Known violations / P1 debt]] | Known violations / P1 debt | 681 |

## Related

Direct project references: [[pages/services/conversation]]. Referenced by [[pages/services/host-console]]. Map: [[pages/architecture/project-map]]. Decision: [[pages/decisions/0037-meetings-kept-locally-in-sqlite]].
