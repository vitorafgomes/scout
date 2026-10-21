---
title: "Plugin.Notes"
version: "1.0"
date: 2026-10-21
changes: "Founded from the code on 2026-10-21"
page_type: service
status: active
description: "Short personal notes the user can add, search, list and delete from chat."
source: ["src/Scout.Plugin.Notes/", "tests/Scout.Plugin.Notes.Tests/"]
reliability: high
---

# Plugin.Notes

> Short personal notes the user can add, search, list and delete from chat.

## To change here, read

- [[pages/invariants/no-executive-writes]]
- [[pages/invariants/pluggable-boundaries]]
- [[pages/services/plugin-common]]
- [[pages/conventions/testing]]

## Public contract

- **Entry point:** `AddNotesPlugin(IServiceCollection, IConfiguration)` at `src/Scout.Plugin.Notes/NotesServiceCollectionExtensions.cs:24`; the plugin is built with `KernelPluginFactory.CreateFromObject` at `src/Scout.Plugin.Notes/NotesServiceCollectionExtensions.cs:42` under the name `Notes`; the host calls it at `src/Scout.Host.Console/HostComposition.cs:95`.
- **Gate:** Enabled read at `src/Scout.Plugin.Notes/NotesServiceCollectionExtensions.cs:34` (default true).
- **Model tools:**
  - `add_note` (`src/Scout.Plugin.Notes/NotesPlugin.cs:39`): appends a note.
  - `search_notes` (`src/Scout.Plugin.Notes/NotesPlugin.cs:81`): text search over notes.
  - `list_recent_notes` (`src/Scout.Plugin.Notes/NotesPlugin.cs:117`): newest notes first.
  - `delete_note` (`src/Scout.Plugin.Notes/NotesPlugin.cs:145`): removes one note by id.
- **Configuration keys:** section `Scout:Plugins:Notes` (`src/Scout.Plugin.Notes/NotesServiceCollectionExtensions.cs:19`), keys `Enabled` (`src/Scout.Plugin.Notes/NotesOptions.cs:13`), `Scout:Plugins:LocalStore:RootDirectory` (`src/Scout.Plugin.Common/ScoutLocalStoreOptions.cs:11`). Names only; values live in the operator's configuration.
- **Data source:** none: Scout's own local store only.

## Write behaviour

Every function is descriptive write, Scout-local only (notes a human reads back; `delete_note` removes Scout's own record). No function writes to an external system or to the host filesystem outside Scout's own store.

## State

Collection `notes` (`src/Scout.Plugin.Notes/NotesPlugin.cs:18`) in the shared `IScoutLocalStore`, one JSON file per collection under `{LocalApplicationData}/Scout/plugin-data` unless `Scout:Plugins:LocalStore:RootDirectory` overrides it (`src/Scout.Plugin.Common/JsonFileScoutLocalStore.cs:30,110`). This is Scout's own storage on the local machine, written atomically (`src/Scout.Plugin.Common/JsonFileScoutLocalStore.cs:103`); see [[pages/services/plugin-common]].

## Invariants

- [[pages/invariants/no-executive-writes]]: writes go only through `IScoutLocalStore`, Scout's own storage; nothing external is mutated.
- [[pages/invariants/pluggable-boundaries]]: a self-gating `AddNotesPlugin` extension contributes one `KernelPlugin` singleton that `src/Scout.Llm/LlmServiceCollectionExtensions.cs:96` adds to the shared kernel; the host still references the project directly (`src/Scout.Host.Console/Scout.Host.Console.csproj`).

## Testing footprint

`tests/Scout.Plugin.Notes.Tests` (31 test attributes, 5 files): NotesPluginMetadataTests, NotesPluginTests, NotesServiceCollectionExtensionsTests. The metadata tests pin the exact function names and require descriptions; the extension tests cover gating and registration.

## Related

Direct project references: [[pages/services/plugin-common]]. Referenced by [[pages/services/host-console]]. Catalogue: [[pages/architecture/plugin-catalogue]]. Map: [[pages/architecture/project-map]].
