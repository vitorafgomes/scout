---
title: "Meetings: Public contract"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Meetings service page."
page_type: service
status: active
description: "Read when a change touches: Public contract."
source: ["src/Scout.Meetings/", "tests/Scout.Meetings.Tests/", "src/Scout.Conversation/Abstractions/IMeetingStore.cs", "Directory.Packages.props", "tests/Scout.ArchTests/PersistenceTests.cs", "context/pages/decisions/0052-persistence-behind-stores-on-entity-framework.md", "ui/packages/main/src/quick-ask/quick-ask.ts", "ui/packages/renderer/src/app/meetings/meetings.component.ts"]
reliability: high
---

## Public contract

- **Entry point:** `AddMeetings(IServiceCollection, IConfiguration)` (`src/Scout.Meetings/MeetingsServiceCollectionExtensions.cs:18`), called by [[pages/services/host-console]]. With `Scout:Meetings:DbPath` set and its parent folder existing (`:30`), it registers `SqliteMeetingStore` as the `IMeetingStore` (`:35`) and `MeetingMarkdown.Render` as a `Func<MeetingRecord, string>` (`:36`), the two together; otherwise nothing, and the feature is off. Resolving the store opens no file.
- **Abstraction:** `IMeetingStore` in `Scout.Conversation` (`src/Scout.Conversation/Abstractions/IMeetingStore.cs:7`, [[pages/services/conversation]]) with `KeepAsync(meeting, keepKey, ct)`, `ListAsync`, `GetAsync`, `DeleteAsync` and `DeleteAllAsync` (`:15` to `:27`), over the records `MeetingRecord`, `MeetingLine`, `MeetingTurn` and `MeetingSummary` (`:31`, `:40`, `:43`, `:46`).
- **Key types:**
  - `SqliteMeetingStore` (`src/Scout.Meetings/SqliteMeetingStore.cs:17`), over the internal `MeetingsDbContext` (`src/Scout.Meetings/Storage/MeetingsDbContext.cs:10`).
  - `MeetingMarkdown.Render` (`src/Scout.Meetings/MeetingMarkdown.cs:15`).
  - `MeetingStoreException` (`src/Scout.Meetings/MeetingStoreException.cs:7`), whose message names the operation and the SQLite error code only.
- **Configuration keys:** `Scout:Meetings:DbPath`, a path, never a credential; the desktop passes `SCOUT__MEETINGS__DBPATH=<userData>/meetings.db` on every worker start (`ui/packages/main/src/worker/build-env.ts:117`), see Desktop side. `appsettings.json` does not name it.
- **Packages:** `Microsoft.EntityFrameworkCore.Sqlite` 10.0.15 (`Directory.Packages.props:43`), which brings `Microsoft.Data.Sqlite` 10.0.15 (`:42`), the version `Scout.Knowledge` references, over `SQLitePCLRaw.bundle_e_sqlite3` 3.0.5 (`:41`); Entity Framework since 2026-11-08 ([[pages/decisions/0052-persistence-behind-stores-on-entity-framework]]), `Microsoft.Data.Sqlite` moved from 9.0.10 the same day; both moved from 10.0.14 to 10.0.15 on 2026-11-12 ([[pages/history/meetings]]).
- **External boundaries:** the database file and its `-wal` and `-shm` companions, under the configured folder. No network: the assembly references no `System.Net` or HTTP assembly (`tests/Scout.Meetings.Tests/SqliteMeetingStoreTests.cs:466`).
