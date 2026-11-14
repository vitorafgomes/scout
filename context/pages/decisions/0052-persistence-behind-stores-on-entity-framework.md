---
title: "ADR 0052 - Persistence behind one store per use case, on Entity Framework Core with SQLite"
version: "1.3"
date: 2026-11-08
changes: "v1.3 (2026-11-13) dated line: the audit job's repository index gains kind and pull_request in a second migration (ADR 0055)."
page_type: adr
status: accepted
description: "Each persistence use case sits behind its own store interface named with the Store suffix; the store implementations reach SQLite through Entity Framework Core only, the DbContext never leaves the store's project, the schema comes from EF migrations applied on the store's first call, a file an older build wrote is deleted and created again, and an ArchTest keeps SQLite, Entity Framework and SQL text inside the stores."
supersedes: ""
superseded_by: ""
source:
  - chat
  - src/Scout.Audit/IAuditJobStore.cs
  - src/Scout.Audit/SqliteAuditStore.cs
  - src/Scout.Audit/Storage/
  - src/Scout.Meetings/SqliteMeetingStore.cs
  - src/Scout.Meetings/Storage/
  - src/Scout.Conversation/Abstractions/IMeetingStore.cs
  - tests/Scout.ArchTests/PersistenceTests.cs
  - Directory.Packages.props
  - .config/dotnet-tools.json
  - src/Scout.Host.Console/StoreWarmupReadinessSignal.cs
reliability: high
---

# ADR 0052 - Persistence behind one store per use case, on Entity Framework Core with SQLite

> Every use case that keeps data has its own store interface; behind it, Entity Framework Core reaches SQLite, and no SQL text or database type exists anywhere else.

## Status

- [ ] Proposed
- [x] Accepted
- [ ] Deprecated / superseded

Updated: 2026-11-08. The owner decided on 2026-11-08; this ADR records the decision and how it was built.

## Context

Two stores kept their rows in SQLite with hand-written SQL: the audit job (`SqliteAuditStore`, [[pages/decisions/0042-audit-as-a-job-with-saved-progress]]), which had no interface and whose callers used the class and its status constants directly, and the kept meetings (`SqliteMeetingStore` behind `IMeetingStore`, [[pages/decisions/0037-meetings-kept-locally-in-sqlite]]). Each carried its schema as a SQL string and migrated by hand (`ALTER TABLE ... ADD COLUMN` checked through `pragma_table_info`). The owner asked for persistence behind one specific interface per use case, never a generic repository, for no SQL queries in code, and for Entity Framework Core, trusting its performance and wanting an easy move to another database later. Scout has no users yet, so the schema starts from scratch and a file an older build wrote needs no migration. "Repository" already means a git repository here (`IRepositoryAudits`, `IRepositorySnapshots`), so the interfaces take the Store suffix.

The project knowledge store ([[pages/services/knowledge]]) is out of scope: it keeps its vectors through the SqliteVec connector and its FTS5 index beside it.

## Decision

- **One store per use case.** `IAuditJobStore` (`src/Scout.Audit/IAuditJobStore.cs:46`) holds exactly the audit job's use cases: create a job, the running jobs, a job, the latest job of a repository, its batches, the next batch, its findings, begin a batch, complete a batch, not covered, stop, pause, resume and finish; the status words move to `AuditStatus` (`:4`), which the callers and the implementation share without naming the SQLite class. `IMeetingStore` stays as it was. `RepositoryAudits`, `AuditWorker` and the DI registration depend on the interface (`src/Scout.Audit/AuditServiceCollectionExtensions.cs:44`). A use case that needs to keep data gets its own interface with its own methods; no generic `IStore<T>`, no base class.
- **Entity Framework Core with SQLite, inside the store only.** `Microsoft.EntityFrameworkCore.Sqlite` 10.0.14 (`Directory.Packages.props:43`) replaces the direct `Microsoft.Data.Sqlite` and `SQLitePCLRaw.bundle_e_sqlite3` references of `Scout.Audit` and `Scout.Meetings`. The DbContext, its row classes and the migrations are internal to the store's project, under its `Storage` folder (`src/Scout.Audit/Storage/AuditDbContext.cs:6`, `src/Scout.Meetings/Storage/MeetingsDbContext.cs:10`); the store builds one context per call from options built once, and no DbContext is registered in DI, so resolving a store opens no file. A write that fails inside `SaveChanges` arrives as `DbUpdateException`; the store unwraps the SQLite error inside it, so the caller still gets `AuditStoreException` or `MeetingStoreException` naming the operation and the SQLite code only. The store classes keep their names, `SqliteAuditStore` and `SqliteMeetingStore`: SQLite is still the provider, and the name says which file format the store writes.
- **Schema by EF migrations, applied on the store's first call.** Each store keeps one task per instance (`src/Scout.Audit/SqliteAuditStore.cs:273`, `src/Scout.Meetings/SqliteMeetingStore.cs:231`): it opens the file for writing, so a path that cannot be a database fails as it opens, then calls `MigrateAsync` only when the build has migrations the file lacks, so a read on a read-only file asks for no write, and sets `journal_mode = WAL`. A failed attempt is not kept; the next call tries again. Future schema changes are new migrations.
- **A database from an older build.** A file with tables and no `__EFMigrationsHistory` rows was written before Entity Framework, or by a first migration that crashed; the store deletes it with its `-wal` and `-shm` files and creates it again from the migrations (`src/Scout.Audit/SqliteAuditStore.cs:301`, `src/Scout.Meetings/SqliteMeetingStore.cs:259`). Its jobs or meetings are gone, which the owner accepted because Scout has no users yet. It is deleted only when every table it holds is one that store knows: the model's tables, Entity Framework's `__EFMigrationsHistory` and `__EFMigrationsLock`, and those the schema before Entity Framework created (`audit_job`, `audit_batch`, `audit_finding`; `schema_version`, `meetings`, `lines`, `turns`, `keeps`). A file with any other table is never deleted, and the call fails with the store's exception, `Scout could not <operation> the ... database: it holds tables Scout did not create, so Scout left it as it is.` (`src/Scout.Audit/SqliteAuditStore.cs:303`, `src/Scout.Meetings/SqliteMeetingStore.cs:261`). A file Scout cannot delete, in a folder it cannot write to, fails with the store's exception naming `UnauthorizedAccessException` or `IOException` and stays where it is. A file that is not a database is never deleted: it fails with SQLite error 26 as before.
- **The SQL that stays, all inside a store.** Entity Framework has no API for these SQLite statements, so they stay in the store implementation, through `ExecuteSqlRawAsync`: `PRAGMA journal_mode = WAL` and, on the first call, the read of `sqlite_master` that lists a file's tables for the older-build check, in both stores, and in the meetings store `PRAGMA secure_delete = ON` on each call's connection and the delete scrub, `VACUUM` and `PRAGMA wal_checkpoint(TRUNCATE)`, which [[pages/decisions/0037-meetings-kept-locally-in-sqlite]] requires so deleted text does not linger. The FTS5 search a later meetings feature brings is the one query in raw SQL this ADR allows, and it goes in the meetings store too.
- **Indexes, in the Initial migrations.** Every query that filters and sorts has an index, since the schema is unreleased they sit in the `Initial` migrations rather than a second one: `audit_job (status, started)` for the running jobs, `audit_job (owner, repo, started)` for the latest job of a repository and the stop, its `owner` and `repo` keeping the columns' `NOCASE` collation so the case-blind comparison can use it, `audit_finding (job_id, seq)` for a job's findings in order, where the plan showed a temporary sort on the `(job_id, merge_key)` index, and `meetings (started_utc_ticks, number)` for the newest-first list (`src/Scout.Audit/Storage/AuditDbContext.cs:21`, `:22`, `:60`, `src/Scout.Meetings/Storage/MeetingsDbContext.cs:35`). The rest was already covered by keys: the primary keys, the alternate keys on `id`, `keeps.meeting_id` and the unique `(job_id, merge_key)`. Measured with SQLite's `EXPLAIN QUERY PLAN` over the SQL Entity Framework generates for each store query: before, `SCAN` plus `USE TEMP B-TREE FOR ORDER BY`; after, `SEARCH ... USING INDEX` (the list a `SCAN ... USING INDEX` that stops at the limit), with no sort.
  - Amended 2026-11-13 by [[pages/decisions/0055-pull-request-review-as-a-job-in-batches]]: the `JobKind` migration replaces `audit_job (owner, repo, started)` with `audit_job (owner, repo, kind, pull_request, started)`, `IX_audit_job_owner_repo_kind_pull_request_started` (`src/Scout.Audit/Storage/Migrations/20261113002741_JobKind.cs:35`), because the latest job and the stop now look a repository up by kind and pull request; `owner` and `repo` keep `NOCASE` and the plan test reads each of the three lookups on it.
- **Warm-up.** The host's readiness signal (`src/Scout.Host.Console/StoreWarmupReadinessSignal.cs:21`, registered at `src/Scout.Host.Console/HostComposition.cs:50`) writes `SCOUT_READY` first, then lists one meeting through `IMeetingStore` on a background task, so the schema step and Entity Framework's first query are paid before the owner's first meetings request; a failure is logged at Debug with the exception type only and the next real call tries again. The audit store is already warmed by the audit worker, which reads the running jobs through `IAuditJobStore` as it starts (`src/Scout.Audit/AuditWorker.cs:67`); neither warm-up blocks readiness. Starting the meetings warm-up when the host starts instead of after readiness was measured and gave nothing: first meetings list at the instant of ready 892 ms against 894 ms, 3 s after 65 ms against 67 ms, ready 417 ms against 415 ms (median of 5, 2026-11-08), so it stays after readiness, where it does not compete with the adapters connecting.
- **Enforced.** `tests/Scout.ArchTests/PersistenceTests.cs` (below).

## Spike, measured before the migration (2026-11-08)

The owner's threshold to continue was a published worker at most 25 MB larger and a startup to readiness at most 300 ms slower. Published with the line `ui/build/scripts/publish-worker.sh` runs (self-contained single file, compressed, not trimmed), `Scout.Host.Console` for linux-x64; startup is the time from spawn to the `SCOUT_READY` line with the local adapter, a meetings path and a GitHub token set (so the audit worker reads its jobs at start), each in a fresh home, median of 5 after one warm-up run.

| | Before | EF package only | Both stores on EF |
|---|---|---|---|
| Worker binary | 85,371,639 bytes | 87,186,456 bytes | 87,206,121 bytes (+1.75 MB) |
| Ready, fresh files | 621 ms | 602 ms | 428 ms |
| Ready, restart over existing files | 396 ms | | 424 ms (+28 ms) |
| First meetings list after ready, fresh files | 120 ms | 120 ms | 1,109 ms |
| First meetings list after ready, existing files | 51 ms | | 904 ms |

Both thresholds hold. The cost lands on the first call of a store after the worker starts, about 850 ms more, which is Entity Framework's cold start (building the model, compiling the queries, the just-in-time compilation of its assemblies in the compressed single file); how it splits between those was not measured.

**Warm-up, measured the same day.** The host now warms the meetings store right after `SCOUT_READY` (Decision, above). First meetings list, restart over existing files, median of 5:

| Request sent | Before EF | EF, no warm-up | EF with warm-up |
|---|---|---|---|
| 3 s after ready | 67 ms | 247 ms | 73 ms |
| at the instant of ready | 51 ms | 904 ms | 881 ms |

A request sent 3 s after ready runs at the earlier speed; one sent at the very instant of ready waits for the warm-up already in flight, since both share the store's one schema step, and still pays about 830 ms. Readiness stays at 414 ms. The compiled model remains the next step if that instant matters.

## Consequences

- **Better:** no SQL statement in a caller and none in a store beyond the SQLite statements listed above; schema changes become migrations instead of hand checks of `pragma_table_info`; a write on a refused file now has a test per store; moving to another database is a provider swap inside two projects.
- **Worse:** a store's first call pays Entity Framework's cold start, about 850 ms (spike above). The meetings store is warmed after readiness and the audit store by the audit worker's start, so only a request in the first second after ready still waits; an EF compiled model (`dotnet ef dbcontext optimize`) and a ReadyToRun publish are the options not measured yet. A keep, measured in process against the raw-SQL store of the build before (median of 5 processes, 2026-11-08): at the limits (5,000 lines of 2,000 characters, 500 turns) 352 ms warm against 138 ms, the first keep after the list warm-up 1,158 ms against 147 ms, the first keep in a cold process 2,117 ms against 207 ms; a meeting of 700 lines and 20 turns 82 ms warm against 8 ms, 336 ms first after the warm-up against 22 ms, 1,310 ms cold against 81 ms. The warm keep at the limits is 2.6 times slower but under half a second, so the insert is left as it is; the list warm-up does not cover the keep path, whose first run still compiles Entity Framework's insert. The schema is checked once per store instance, so a database file deleted while the worker runs is created again only at the next start, where the hand-written schema recreated it on the next call; what the calls in between answer was not tested.
- **Neutral but noteworthy:** migrations add generated files, kept as the procedure below leaves them.

## Adding a migration

`dotnet ef` is pinned at 10.0.14, the EF version, by the local tool manifest `.config/dotnet-tools.json`; `dotnet tool restore` installs it. The Design package never enters the build, so the tool runs against a scratch startup project. To add one after a model change:

1. Add a temporary `IDesignTimeDbContextFactory<TContext>` in the store project's `Storage` folder that builds the context with `UseSqlite("Data Source=design.db")`.
2. Create a scratch console project outside the repository that references the store project and `Microsoft.EntityFrameworkCore.Design` at the EF version, plus `Microsoft.CodeAnalysis.Common`, `Microsoft.CodeAnalysis.CSharp.Workspaces` and `Microsoft.CodeAnalysis.Workspaces.Common` at the repository's Roslyn version (5.9.0 today), or restore fails on a version conflict.
3. `dotnet tool restore`, then `dotnet ef migrations add <Name> --project src/<Project>/<Project>.csproj --startup-project <scratch>.csproj --context <Context> --output-dir Storage/Migrations --namespace <Project>.Storage.Migrations`.
4. The tool writes the `ModelSnapshot` under a folder named after the namespace (`src/<Project>/Scout/...`); move it into `Storage/Migrations` and delete that folder.
5. Tidy the new migration file for the analyzers: file-scoped namespace, no unused using, a constant array as a static field, and the class `internal` (Entity Framework finds migrations of the same assembly whatever their accessibility). It carries no generator line, and the repository forbids a nested `generated_code` opt-out (`tests/Scout.ArchTests/AnalyzerGateTests.cs:95`). Remove the factory and build at 0 warnings.
6. Leave the `Designer` and `ModelSnapshot` files as the tool wrote them, with their `// <auto-generated />` first line: it is the generator's marker that keeps the analyzers off generated code, not a comment about the code.

## Moving to another database

Swap `UseSqlite` for the other provider in the two stores' constructors, regenerate the migrations for it, and re-decide what is SQLite-specific: the `NOCASE` collation on the audit job's owner and repository, the `Sqlite:Autoincrement` annotation on the order columns, the UTC-ticks column the meetings list sorts by (SQLite cannot order a `DateTimeOffset`), the `ExecuteSqlRawAsync` statements above, the older-build detection (it uses `HasTablesAsync` and the history table) and, for meetings, the delete scrub: that deleted text leaves no trace is a property of the SQLite file, and another engine needs its own answer before meetings move. The callers do not change: they see the store interfaces and records only.

## Alternatives considered

- **Keep the hand-written SQL behind the new interface** - rejected by the owner: no SQL queries in code.
- **A generic repository (`IRepository<T>`) over the context** - rejected: one interface per use case, specific; and "repository" means a git repository in Scout.
- **`EnsureCreated` instead of migrations** - rejected: a later schema change would again need hand-written upgrades or a recreate; migrations keep the path open.
- **Dapper or another micro-ORM** - rejected: still SQL in code.
- **Moving the knowledge store too** - out of scope: it keeps its vectors on the SqliteVec connector.

## Enforcement

- `tests/Scout.ArchTests/PersistenceTests.cs:66`: no type in a Scout library outside the store classes and their `Storage` namespaces (and the knowledge store) depends on `Microsoft.Data.Sqlite` or `Microsoft.EntityFrameworkCore`; `:79`: no source file under `src/` outside the store files holds `SELECT `, `INSERT INTO`, `UPDATE `, `DELETE FROM`, `CREATE TABLE`, `ALTER TABLE` or `PRAGMA `; `:92`: no source file outside them names either namespace, which covers the hosts the type rule does not load; `:105`, `:120`, `:129`: the rules see the stores they allow, and the detector and allow-list tests.
- The older-build guard: per store, a file in a folder Scout cannot write to fails with the store's exception and stays, and a file with a table Scout did not create is never deleted and the call says why (`tests/Scout.Audit.Tests/SqliteAuditStoreTests.cs:189`, `:208`, `tests/Scout.Meetings.Tests/SqliteMeetingStoreTests.cs:422`, `:440`).
- The indexes: per store, SQLite's plan for each query's own SQL names its index and holds neither a table scan nor a temporary sort (`tests/Scout.Audit.Tests/SqliteAuditStoreTests.cs:288`, `tests/Scout.Meetings.Tests/SqliteMeetingStoreTests.cs:453`).
- The warm-up: ready first, then one list through the interface; a failure logged at Debug without the store's text; nothing to warm without a meetings store; and through the composed host, the meetings database migrated after ready before any meetings request (`tests/Scout.Host.Console.Tests/StoreWarmupReadinessSignalTests.cs:32`, `:50`, `:70`, `:83`).
- The stores' existing tests unchanged, plus per store a write the file refuses (SQLite error 8, a read-only file) and a file an older build wrote: `tests/Scout.Audit.Tests/SqliteAuditStoreTests.cs:261`, `:117`, `tests/Scout.Meetings.Tests/SqliteMeetingStoreTests.cs:353`, `:370`.

## Related

- Services affected: [[pages/services/audit]], [[pages/services/meetings]].
- Amends: [[pages/decisions/0037-meetings-kept-locally-in-sqlite]] (amendment of 2026-11-08). Related: [[pages/decisions/0042-audit-as-a-job-with-saved-progress]], whose `audit.db` schema this store now creates through a migration.
- Not affected: [[pages/services/knowledge]].
- Conventions: [[pages/conventions/testing]].

## Amendment: the control plane's JSON files (2026-11-10)

**Status: Proposed 2026-11-10, waiting for the owner.** It changes the scope of this decision, so it holds only once the owner accepts it.

The Decision above says every use case that keeps data gets a store interface with Entity Framework Core over SQLite behind it, and scopes out only the knowledge store. The control plane keeps six use cases in JSON files beside `features.json`, each behind its own interface but with no database: `JsonFileActionQueue` (`actions.json`), `JsonLinesWriteLog` (`writes.log`), `JsonFileFeatureStateStore` (`features.json`), `JsonFileOperatorStore` (`mandate.json`, `projects.json`, `readiness.json`, `comments.json`), `JsonFileLocalRuntimeConfigStore` (`local-runtimes.json`) and, since 2026-11-10, `JsonFileOwnerControlQueue` (`controls.json`, [[pages/services/control-plane]]). All but the last predate this decision, which did not name them.

- **What this amendment does.** It names those six as outside this decision for now, and nothing else: no new file store, and no move of the existing ones in this change.
- **The reason.** No page records why the control plane keeps its state in JSON files rather than SQLite; [[pages/decisions/0032-per-feature-write-permissions]] only says the queue and the log are "Scout's own files beside `features.json`". What the files give today, read from the code: one writer per file under a gate, an atomic replace through a temporary file, a malformed file moved aside, and a text the owner can open. That is a description, not a decision, so the scope-out is temporary: [[pages/roadmap/followups]] carries the entry that brings the six behind stores on Entity Framework or records the owner's reason to keep them.
- **Enforced.** `tests/Scout.ArchTests/PersistenceTests.cs:85`: the classes named `JsonFile…` or `JsonLines…` in the Scout libraries are exactly these six, so a seventh cannot appear without a change to this amendment; `:91` shows the scan sees one more.

## Amendment: Entity Framework Core 10.0.15 (2026-11-12)

The versions the Decision and "Adding a migration" name are now 10.0.15: `Microsoft.EntityFrameworkCore.Sqlite` (`Directory.Packages.props:43`), `Microsoft.Data.Sqlite` (`:42`) and the `dotnet ef` tool in `.config/dotnet-tools.json`, the scratch startup project's `Microsoft.EntityFrameworkCore.Design` with them. No migration was added: `dotnet ef migrations has-pending-model-changes` answered "No changes have been made to the model since the last migration." for `AuditDbContext` and `MeetingsDbContext` on 10.0.15. The check left `src/` untouched: each scratch startup project took the assembly name of the store's test project, `Scout.Audit.Tests` or `Scout.Meetings.Tests`, which the store's `InternalsVisibleTo` already names, so the design-time factory for the internal context lived in the scratch project. Entity Framework Core 11.0.0 targets `net11.0` only, so it waits for the target framework to move ([[pages/roadmap/followups]]).
