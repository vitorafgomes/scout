---
title: "Plugin.Reminders"
version: "1.0"
date: 2026-10-21
changes: "Founded from the code on 2026-10-21"
page_type: service
status: active
description: "Personal reminders the user can add, list, complete and delete; pull-based, no proactive delivery."
source: ["src/Scout.Plugin.Reminders/", "tests/Scout.Plugin.Reminders.Tests/"]
reliability: high
---

# Plugin.Reminders

> Personal reminders the user can add, list, complete and delete; pull-based, no proactive delivery.

## To change here, read

- [[pages/invariants/no-executive-writes]]
- [[pages/invariants/pluggable-boundaries]]
- [[pages/services/plugin-common]]
- [[pages/conventions/testing]]

## Public contract

- **Entry point:** `AddRemindersPlugin(IServiceCollection, IConfiguration)` at `src/Scout.Plugin.Reminders/RemindersServiceCollectionExtensions.cs:26`; the plugin is built with `KernelPluginFactory.CreateFromObject` at `src/Scout.Plugin.Reminders/RemindersServiceCollectionExtensions.cs:44` under the name `Reminders`; the host calls it at `src/Scout.Host.Console/HostComposition.cs:100`.
- **Gate:** Enabled read at `src/Scout.Plugin.Reminders/RemindersServiceCollectionExtensions.cs:36` (default true).
- **Model tools:**
  - `add_reminder` (`src/Scout.Plugin.Reminders/RemindersPlugin.cs:63`): stores a reminder with a parsed due date.
  - `list_reminders` (`src/Scout.Plugin.Reminders/RemindersPlugin.cs:120`): lists open reminders.
  - `complete_reminder` (`src/Scout.Plugin.Reminders/RemindersPlugin.cs:168`): marks one done.
  - `delete_reminder` (`src/Scout.Plugin.Reminders/RemindersPlugin.cs:227`): removes one.
- **Configuration keys:** section `Scout:Plugins:Reminders` (`src/Scout.Plugin.Reminders/RemindersServiceCollectionExtensions.cs:21`), keys `Enabled` (`src/Scout.Plugin.Reminders/RemindersOptions.cs:13`), `TimeZone` (`src/Scout.Plugin.Reminders/RemindersOptions.cs:19`), `Scout:Plugins:LocalStore:RootDirectory` (`src/Scout.Plugin.Common/ScoutLocalStoreOptions.cs:11`). Names only; values live in the operator's configuration.
- **Data source:** none: Scout's own local store only. The class comment states there are no proactive pings and recurrence is stored for display, not acted upon (`src/Scout.Plugin.Reminders/RemindersPlugin.cs:13`).

## Write behaviour

Every function is descriptive write, Scout-local only. No function writes to an external system or to the host filesystem outside Scout's own store.

## State

Collection `reminders` (`src/Scout.Plugin.Reminders/RemindersPlugin.cs:22`) in the shared `IScoutLocalStore`, one JSON file per collection under `{LocalApplicationData}/Scout/plugin-data` unless `Scout:Plugins:LocalStore:RootDirectory` overrides it (`src/Scout.Plugin.Common/JsonFileScoutLocalStore.cs:30,110`). This is Scout's own storage on the local machine, written atomically (`src/Scout.Plugin.Common/JsonFileScoutLocalStore.cs:103`); see [[pages/services/plugin-common]].

## Invariants

- [[pages/invariants/no-executive-writes]]: writes go only through `IScoutLocalStore`, Scout's own storage; nothing external is mutated.
- [[pages/invariants/pluggable-boundaries]]: a self-gating `AddRemindersPlugin` extension contributes one `KernelPlugin` singleton that `src/Scout.Llm/LlmServiceCollectionExtensions.cs:96` adds to the shared kernel; the host still references the project directly (`src/Scout.Host.Console/Scout.Host.Console.csproj`).

## Testing footprint

`tests/Scout.Plugin.Reminders.Tests` (43 test attributes, 5 files): RemindersPluginMetadataTests, RemindersPluginTests, RemindersServiceCollectionExtensionsTests. The metadata tests pin the exact function names and require descriptions; the extension tests cover gating and registration.

## Related

Direct project references: [[pages/services/plugin-common]]. Referenced by [[pages/services/host-console]]. Catalogue: [[pages/architecture/plugin-catalogue]]. Map: [[pages/architecture/project-map]].
