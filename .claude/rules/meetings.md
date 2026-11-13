---
paths:
  - "src/Scout.Meetings/**"
  - "tests/Scout.Meetings.Tests/**"
  - "src/Scout.Conversation/Abstractions/IMeetingStore.cs"
  - "tests/Scout.ArchTests/PersistenceTests.cs"
  - "ui/packages/main/src/quick-ask/quick-ask.ts"
  - "ui/packages/renderer/src/app/meetings/meetings.component.ts"
---

# Meetings

Owns the meetings database: its schema and its migrations, keeping a meeting with its lines and turns in one transaction, listing, reading, deleting with the text scrubbed from the file, the limits of a meeting, and the Markdown document of one. Does not own when a meeting is kept, the wire requests or their sentences ([[pages/services/adapter-local]]), nor the Stop row's "Keep this meeting", the Meetings screen or the file the owner saves the export to (the desktop, [[pages/services/desktop-quick-ask]]).

## Rules this project must keep

- [[pages/invariants/no-executive-writes]]: the only writes are to the database the configuration names, Scout's own data folder, and only for a meeting the owner chose to keep in the desktop; nothing leaves the process. The model has no function that reaches the store.
- [[pages/invariants/pluggable-boundaries]]: no Sqlite or Entity Framework type leaves the store and its `Storage` namespace (`tests/Scout.ArchTests/PersistenceTests.cs:66`, `:92`); the adapter sees `IMeetingStore` and a `Func<MeetingRecord, string>`, both over Conversation records.
- [[pages/invariants/local-first-default]]: works offline, with no account and no provider.
- [[pages/invariants/secrets-never-in-source]]: no credential; the exception messages carry no meeting text.

## Pages that govern this project

Read one when your change touches what its title names.

- [[pages/decisions/0037-meetings-kept-locally-in-sqlite|ADR 0037 - Meetings kept locally in SQLite: only when the owner keeps one, under Scout's data folder, deleted with VACUUM]]
- [[pages/decisions/0052-persistence-behind-stores-on-entity-framework|ADR 0052 - Persistence behind one store per use case, on Entity Framework Core with SQLite]] - Entity Framework behind the store, its migrations
- [[pages/invariants/no-executive-writes|Scout performs descriptive writes only]]
- [[pages/invariants/pluggable-boundaries|Adapters, LLM providers and plugins are swapped by configuration]]
- [[pages/invariants/local-first-default|Ollama is the default LLM provider]]
- [[pages/invariants/secrets-never-in-source|Secrets never live in source]]
- [[pages/roadmap/live-assistant|Live assistant: hear, see and translate for the owner]] - phase 6, the meeting session
- [[pages/services/adapter-local|Adapter.Local]] - the requests that reach the store
- [[pages/conventions/testing|Testing]]

## What to read for a change

Find the row your change matches and read that page. Read one or two rows, not the table.

| If the change touches | Read |
|---|---|
| Public contract | `context/pages/services/meetings/public-contract.md` |
| How it works | `context/pages/services/meetings/how-it-works.md` |
| Desktop side | `context/pages/services/meetings/desktop-side.md` |
| Hot spots | `context/pages/services/meetings/hot-spots.md` |
| Cache footprint | `context/pages/services/meetings/cache-footprint.md` |
| Cross-service dependencies | `context/pages/services/meetings/cross-service-dependencies.md` |
| Testing footprint | `context/pages/services/meetings/testing-footprint.md` |
| Known violations / P1 debt | `context/pages/services/meetings/known-violations-p1-debt.md` |

For where a type lives or who calls it, ask the code graph (`graphify explain "<Type>"`, `graphify affected "<Type>"`).
