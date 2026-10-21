---
title: "Plugin.Lists"
version: "1.0"
date: 2026-10-21
changes: "Founded from the code on 2026-10-21"
page_type: service
status: active
description: "Named lists (shopping, to-do) the user can add to, show, remove from and clear."
source: ["src/Scout.Plugin.Lists/", "tests/Scout.Plugin.Lists.Tests/"]
reliability: high
---

# Plugin.Lists

> Named lists (shopping, to-do) the user can add to, show, remove from and clear.

## To change here, read

- [[pages/invariants/no-executive-writes]]
- [[pages/invariants/pluggable-boundaries]]
- [[pages/services/plugin-common]]
- [[pages/conventions/testing]]

## Public contract

- **Entry point:** `AddListsPlugin(IServiceCollection, IConfiguration)` at `src/Scout.Plugin.Lists/ListsServiceCollectionExtensions.cs:24`; the plugin is built with `KernelPluginFactory.CreateFromObject` at `src/Scout.Plugin.Lists/ListsServiceCollectionExtensions.cs:42` under the name `Lists`; the host calls it at `src/Scout.Host.Console/HostComposition.cs:90`.
- **Gate:** Enabled read at `src/Scout.Plugin.Lists/ListsServiceCollectionExtensions.cs:34` (default true).
- **Model tools:**
  - `add_to_list` (`src/Scout.Plugin.Lists/ListsPlugin.cs:37`): adds an item to a named list.
  - `show_list` (`src/Scout.Plugin.Lists/ListsPlugin.cs:84`): prints a list.
  - `remove_from_list` (`src/Scout.Plugin.Lists/ListsPlugin.cs:119`): removes one item.
  - `clear_list` (`src/Scout.Plugin.Lists/ListsPlugin.cs:189`): empties a list.
- **Configuration keys:** section `Scout:Plugins:Lists` (`src/Scout.Plugin.Lists/ListsServiceCollectionExtensions.cs:19`), keys `Enabled` (`src/Scout.Plugin.Lists/ListsOptions.cs:14`), `Scout:Plugins:LocalStore:RootDirectory` (`src/Scout.Plugin.Common/ScoutLocalStoreOptions.cs:11`). Names only; values live in the operator's configuration.
- **Data source:** none: Scout's own local store only.

## Write behaviour

Every function is descriptive write, Scout-local only (`clear_list` and `remove_from_list` delete Scout's own records). No function writes to an external system or to the host filesystem outside Scout's own store.

## State

Collection `lists` (`src/Scout.Plugin.Lists/ListsPlugin.cs:20`) in the shared `IScoutLocalStore`, one JSON file per collection under `{LocalApplicationData}/Scout/plugin-data` unless `Scout:Plugins:LocalStore:RootDirectory` overrides it (`src/Scout.Plugin.Common/JsonFileScoutLocalStore.cs:30,110`). This is Scout's own storage on the local machine, written atomically (`src/Scout.Plugin.Common/JsonFileScoutLocalStore.cs:103`); see [[pages/services/plugin-common]].

## Invariants

- [[pages/invariants/no-executive-writes]]: writes go only through `IScoutLocalStore`, Scout's own storage; nothing external is mutated.
- [[pages/invariants/pluggable-boundaries]]: a self-gating `AddListsPlugin` extension contributes one `KernelPlugin` singleton that `src/Scout.Llm/LlmServiceCollectionExtensions.cs:96` adds to the shared kernel; the host still references the project directly (`src/Scout.Host.Console/Scout.Host.Console.csproj`).

## Testing footprint

`tests/Scout.Plugin.Lists.Tests` (32 test attributes, 4 files): ListsPluginMetadataTests, ListsPluginTests, ListsServiceCollectionExtensionsTests. The metadata tests pin the exact function names and require descriptions; the extension tests cover gating and registration.

## Related

Direct project references: [[pages/services/plugin-common]]. Referenced by [[pages/services/host-console]]. Catalogue: [[pages/architecture/plugin-catalogue]]. Map: [[pages/architecture/project-map]].
