---
title: "Meetings - history"
version: "1.0"
date: 2026-11-08
changes: "Initial history note: the meetings store on Entity Framework Core"
page_type: reference
status: artefact
description: "What changed in the meetings store and when."
source:
  - chat
  - src/Scout.Meetings/SqliteMeetingStore.cs
  - tests/Scout.Meetings.Tests/SqliteMeetingStoreTests.cs
reliability: high
---

# Meetings - history

> Dated changes to [[pages/services/meetings]]; the service page states how it works now.

## 2026-11-12 - Entity Framework Core 10.0.14 to 10.0.15

- **Asked for:** the owner's "update all the DLLs that are out of date".
- **What changed:** `Microsoft.EntityFrameworkCore.Sqlite`, `Microsoft.Data.Sqlite` and the `dotnet ef` tool moved to the 10.0.15 servicing release with the rest of the .NET 10 packages; no code and no migration changed, and `dotnet ef migrations has-pending-model-changes` reports no pending change ([[pages/decisions/0052-persistence-behind-stores-on-entity-framework]], amendment of 2026-11-12). The 11.0.0 line targets `net11.0` only and is held back ([[pages/roadmap/followups]]).
- **Tests:** `Scout.Audit.Tests` (180) and `Scout.Meetings.Tests` (32) green at their earlier totals.

## 2026-11-11 - Delete all answers its count

- **Asked for:** an automated audit of main (issue #265, "Delete-all loads every meeting before deleting"): the local adapter listed every meeting, `ListAsync(int.MaxValue)`, only to count them before `DeleteAllAsync`.
- **What changed:** `IMeetingStore.DeleteAllAsync` answers how many meetings it deleted, the count of the store's one `DELETE`, and the adapter answers that count. The count and the delete are now one statement, where the list and the delete were two.
- **Tests:** `Delete_All_Answers_The_Count_The_Delete_Reports_Without_Listing_Every_Meeting` in `LocalChatAdapterTests.cs`, red with the listing put back; `Delete_All_Answers_How_Many_Meetings_It_Deleted_Not_Their_Lines_Or_Turns` in `SqliteMeetingStoreTests.cs`, red with the store answering 0.

## 2026-11-08 - The store on Entity Framework Core

- **Asked for:** no SQL queries in code, Entity Framework Core ([[pages/decisions/0052-persistence-behind-stores-on-entity-framework]]).
- **What changed:** `SqliteMeetingStore` reaches `meetings.db` through an internal DbContext and an `Initial` migration instead of SQL strings; the list sorts by a UTC-ticks column instead of `julianday(started_at)`, and by an order column instead of `rowid`; the schema is checked on the store's first call instead of on every open; a file an older build wrote is deleted and created again, its meetings lost. The `Initial` migration creates `meetings (started_utc_ticks, number)`, so the list no longer scans and sorts the table. An older build's file is deleted only when every table it holds is a known one, and a failure to delete it is a `MeetingStoreException`; the Initial migration class is internal. The pragmas and the delete scrub stay as SQL inside the store. The host warms the store right after `SCOUT_READY` with one list through `IMeetingStore`, so a meetings request 3 s after ready answered in 73 ms against 247 ms without it (67 ms before Entity Framework; median of 5).
- **Tests:** every existing test unchanged; `A_Keep_The_Database_Refuses_To_Write_Throws_Without_The_Meeting_Text_And_Stores_Nothing` and `A_Database_An_Older_Build_Wrote_Without_The_Migration_History_Is_Created_Again` are new; the warm-up is covered by `tests/Scout.Host.Console.Tests/StoreWarmupReadinessSignalTests.cs`.

## Version notes: `pages/services/meetings.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- Entity Framework Core and `Microsoft.Data.Sqlite` on 10.0.15 (2026-11-12). Earlier: Delete all answers how many meetings it deleted, the count of its one DELETE, so the local adapter reports it without listing every meeting; citations past the delete re-pointed by content (2026-11-11). Earlier: The store on Entity Framework Core with an Initial migration, a file an older build wrote created again, a refused write as a sentence (ADR 0052, 2026-11-08). Earlier: Microsoft.Data.Sqlite 10.0.14 (2026-11-08), from 9.0.10
