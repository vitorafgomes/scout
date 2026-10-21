---
title: "Plugin.Dates"
version: "1.0"
date: 2026-10-21
changes: "Founded from the code on 2026-10-21"
page_type: service
status: active
description: "Date arithmetic and the current date/time in a configured zone, computed locally."
source: ["src/Scout.Plugin.Dates/", "tests/Scout.Plugin.Dates.Tests/"]
reliability: high
---

# Plugin.Dates

> Date arithmetic and the current date/time in a configured zone, computed locally.

## To change here, read

- [[pages/invariants/no-executive-writes]]
- [[pages/invariants/pluggable-boundaries]]
- [[pages/services/plugin-common]]
- [[pages/conventions/testing]]

## Public contract

- **Entry point:** `AddDatesPlugin(IServiceCollection, IConfiguration)` at `src/Scout.Plugin.Dates/DatesServiceCollectionExtensions.cs:25`; the plugin is built with `KernelPluginFactory.CreateFromObject` at `src/Scout.Plugin.Dates/DatesServiceCollectionExtensions.cs:52` under the name `Dates`; the host calls it at `src/Scout.Host.Console/HostComposition.cs:93`.
- **Gate:** Enabled read at `src/Scout.Plugin.Dates/DatesServiceCollectionExtensions.cs:35` (default true).
- **Model tools:**
  - `today` (`src/Scout.Plugin.Dates/DatesPlugin.cs:35`): current date in the configured zone.
  - `current_time` (`src/Scout.Plugin.Dates/DatesPlugin.cs:58`): current time in the configured zone.
  - `days_until` (`src/Scout.Plugin.Dates/DatesPlugin.cs:83`): days from today to a date parsed by `RelaxedDate`.
  - `day_of_week` (`src/Scout.Plugin.Dates/DatesPlugin.cs:118`): weekday of a parsed date.
  - `date_difference` (`src/Scout.Plugin.Dates/DatesPlugin.cs:145`): difference between two parsed dates.
  - `add_to_date` (`src/Scout.Plugin.Dates/DatesPlugin.cs:183`): adds a span to a parsed date.
- **Configuration keys:** section `Scout:Plugins:Dates` (`src/Scout.Plugin.Dates/DatesServiceCollectionExtensions.cs:20`), keys `Enabled` (`src/Scout.Plugin.Dates/DatesOptions.cs:14`), `DefaultTimeZone` (`src/Scout.Plugin.Dates/DatesOptions.cs:22`). Names only; values live in the operator's configuration.
- **Data source:** none: injected `TimeProvider` and `RelaxedDate` from Plugin.Common (`src/Scout.Plugin.Common/RelaxedDate.cs:82`).

## Write behaviour

Every function is read-only (pure functions). No function writes to an external system or to the host filesystem outside Scout's own store.

## Invariants

- [[pages/invariants/no-executive-writes]]: no write method exists; every function is a read.
- [[pages/invariants/pluggable-boundaries]]: a self-gating `AddDatesPlugin` extension contributes one `KernelPlugin` singleton that `src/Scout.Llm/LlmServiceCollectionExtensions.cs:96` adds to the shared kernel; the host still references the project directly (`src/Scout.Host.Console/Scout.Host.Console.csproj`).

## Testing footprint

`tests/Scout.Plugin.Dates.Tests` (39 test attributes, 5 files): DatesPluginMetadataTests, DatesPluginTests, DatesServiceCollectionExtensionsTests. The metadata tests pin the exact function names and require descriptions; the extension tests cover gating and registration.

## Related

Direct project references: [[pages/services/plugin-common]]. Referenced by [[pages/services/host-console]]. Catalogue: [[pages/architecture/plugin-catalogue]]. Map: [[pages/architecture/project-map]].
