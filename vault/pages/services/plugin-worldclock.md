---
title: "Plugin.WorldClock"
version: "1.0"
date: 2026-10-21
changes: "Founded from the code on 2026-10-21"
page_type: service
status: active
description: "Current time in a city and conversion between cities, resolved from the local time-zone database; no network."
source: ["src/Scout.Plugin.WorldClock/", "tests/Scout.Plugin.WorldClock.Tests/"]
reliability: high
---

# Plugin.WorldClock

> Current time in a city and conversion between cities, resolved from the local time-zone database; no network.

## To change here, read

- [[pages/invariants/no-executive-writes]]
- [[pages/invariants/pluggable-boundaries]]
- [[pages/services/plugin-common]]
- [[pages/conventions/testing]]

## Public contract

- **Entry point:** `AddWorldClockPlugin(IServiceCollection, IConfiguration)` at `src/Scout.Plugin.WorldClock/WorldClockServiceCollectionExtensions.cs:28`; the plugin is built with `KernelPluginFactory.CreateFromObject` at `src/Scout.Plugin.WorldClock/WorldClockServiceCollectionExtensions.cs:57` under the name `WorldClock`; the host calls it at `src/Scout.Host.Console/HostComposition.cs:101`.
- **Gate:** Enabled read at `src/Scout.Plugin.WorldClock/WorldClockServiceCollectionExtensions.cs:38` (default true).
- **Model tools:**
  - `current_time_in_city` (`src/Scout.Plugin.WorldClock/WorldClockPlugin.cs:60`): current time in a named city.
  - `convert_time_between_cities` (`src/Scout.Plugin.WorldClock/WorldClockPlugin.cs:112`): converts a time from one city to another.
  - `time_difference_between_cities` (`src/Scout.Plugin.WorldClock/WorldClockPlugin.cs:175`): offset between two cities.
- **Configuration keys:** section `Scout:Plugins:WorldClock` (`src/Scout.Plugin.WorldClock/WorldClockServiceCollectionExtensions.cs:23`), keys `Enabled` (`src/Scout.Plugin.WorldClock/WorldClockOptions.cs:15`), `DefaultTimeZone` (`src/Scout.Plugin.WorldClock/WorldClockOptions.cs:22`). Names only; values live in the operator's configuration.
- **Data source:** none: city names map to system time zones in `src/Scout.Plugin.WorldClock/CityZoneResolver.cs`; no HttpClient is registered.

## Write behaviour

Every function is read-only (pure functions). No function writes to an external system or to the host filesystem outside Scout's own store.

## Invariants

- [[pages/invariants/no-executive-writes]]: no write method exists; every function is a read.
- [[pages/invariants/pluggable-boundaries]]: a self-gating `AddWorldClockPlugin` extension contributes one `KernelPlugin` singleton that `src/Scout.Llm/LlmServiceCollectionExtensions.cs:96` adds to the shared kernel; the host still references the project directly (`src/Scout.Host.Console/Scout.Host.Console.csproj`).

## Testing footprint

`tests/Scout.Plugin.WorldClock.Tests` (45 test attributes, 6 files): WorldClockBoundaryTests, WorldClockPluginMetadataTests, WorldClockPluginTests, WorldClockServiceCollectionExtensionsTests. The metadata tests pin the exact function names and require descriptions; the extension tests cover gating and registration.

## Related

Direct project references: [[pages/services/plugin-common]]. Referenced by [[pages/services/host-console]]. Catalogue: [[pages/architecture/plugin-catalogue]]. Map: [[pages/architecture/project-map]].
