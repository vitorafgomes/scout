---
title: "Plugin.Sports"
version: "1.0"
date: 2026-10-21
changes: "Founded from the code on 2026-10-21"
page_type: service
status: active
description: "Last result, next fixture and league table from TheSportsDB."
source: ["src/Scout.Plugin.Sports/", "tests/Scout.Plugin.Sports.Tests/"]
reliability: high
---

# Plugin.Sports

> Last result, next fixture and league table from TheSportsDB.

## To change here, read

- [[pages/invariants/no-executive-writes]]
- [[pages/invariants/pluggable-boundaries]]
- [[pages/services/plugin-common]]
- [[pages/conventions/testing]]

## Public contract

- **Entry point:** `AddSportsPlugin(IServiceCollection, IConfiguration)` at `src/Scout.Plugin.Sports/SportsServiceCollectionExtensions.cs:33`; the plugin is built with `KernelPluginFactory.CreateFromObject` at `src/Scout.Plugin.Sports/SportsServiceCollectionExtensions.cs:68` under the name `Sports`; the host calls it at `src/Scout.Host.Console/HostComposition.cs:112`.
- **Gate:** Enabled read at `src/Scout.Plugin.Sports/SportsServiceCollectionExtensions.cs:43` (default true).
- **Model tools:**
  - `get_last_result` (`src/Scout.Plugin.Sports/SportsPlugin.cs:48`): last events of a team (`src/Scout.Plugin.Sports/SportsClient.cs:37`).
  - `get_next_fixture` (`src/Scout.Plugin.Sports/SportsPlugin.cs:89`): next events of a team (`src/Scout.Plugin.Sports/SportsClient.cs:44`).
  - `get_league_table` (`src/Scout.Plugin.Sports/SportsPlugin.cs:129`): standings of a league (`src/Scout.Plugin.Sports/SportsClient.cs:65`).
- **Configuration keys:** section `Scout:Plugins:Sports` (`src/Scout.Plugin.Sports/SportsServiceCollectionExtensions.cs:24`), keys `Enabled` (`src/Scout.Plugin.Sports/SportsOptions.cs:14`), `FootballDataToken` (`src/Scout.Plugin.Sports/SportsOptions.cs:21`), `TimeZone` (`src/Scout.Plugin.Sports/SportsOptions.cs:27`). Names only; values live in the operator's configuration.
- **Data source:** HTTPS GET to TheSportsDB; the API key is part of the base-address path (`src/Scout.Plugin.Sports/SportsServiceCollectionExtensions.cs:26,54`). When `FootballDataToken` is blank the public free key constant at `:28` is used (a published test value, not a secret). The option name says Football-Data but the value feeds TheSportsDB.

## Write behaviour

Every function is read-only. No function writes to an external system or to the host filesystem outside Scout's own store.

## Invariants

- [[pages/invariants/no-executive-writes]]: no write method exists; every function is a read.
- [[pages/invariants/pluggable-boundaries]]: a self-gating `AddSportsPlugin` extension contributes one `KernelPlugin` singleton that `src/Scout.Llm/LlmServiceCollectionExtensions.cs:96` adds to the shared kernel; the host still references the project directly (`src/Scout.Host.Console/Scout.Host.Console.csproj`).

## Testing footprint

`tests/Scout.Plugin.Sports.Tests` (50 test attributes, 6 files): SportsClientTests, SportsPluginMetadataTests, SportsPluginTests, SportsServiceCollectionExtensionsTests. The metadata tests pin the exact function names and require descriptions; the extension tests cover gating and registration.

## Related

Direct project references: [[pages/services/plugin-common]]. Referenced by [[pages/services/host-console]]. Catalogue: [[pages/architecture/plugin-catalogue]]. Map: [[pages/architecture/project-map]].
