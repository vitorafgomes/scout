---
title: "Plugin.Holidays"
version: "1.0"
date: 2026-10-21
changes: "Founded from the code on 2026-10-21"
page_type: service
status: active
description: "Public holidays and long weekends by country from the Nager.Date public API."
source: ["src/Scout.Plugin.Holidays/", "tests/Scout.Plugin.Holidays.Tests/"]
reliability: high
---

# Plugin.Holidays

> Public holidays and long weekends by country from the Nager.Date public API.

## To change here, read

- [[pages/invariants/no-executive-writes]]
- [[pages/invariants/pluggable-boundaries]]
- [[pages/services/plugin-common]]
- [[pages/conventions/testing]]

## Public contract

- **Entry point:** `AddHolidaysPlugin(IServiceCollection, IConfiguration)` at `src/Scout.Plugin.Holidays/HolidaysServiceCollectionExtensions.cs:30`; the plugin is built with `KernelPluginFactory.CreateFromObject` at `src/Scout.Plugin.Holidays/HolidaysServiceCollectionExtensions.cs:58` under the name `Holidays`; the host calls it at `src/Scout.Host.Console/HostComposition.cs:94`.
- **Gate:** Enabled read at `src/Scout.Plugin.Holidays/HolidaysServiceCollectionExtensions.cs:40` (default true).
- **Model tools:**
  - `get_next_holidays` (`src/Scout.Plugin.Holidays/HolidaysPlugin.cs:44`): upcoming holidays (`src/Scout.Plugin.Holidays/HolidaysClient.cs:26`).
  - `is_holiday` (`src/Scout.Plugin.Holidays/HolidaysPlugin.cs:81`): whether a parsed date is a holiday (`src/Scout.Plugin.Holidays/HolidaysClient.cs:34`).
  - `list_holidays` (`src/Scout.Plugin.Holidays/HolidaysPlugin.cs:121`): all holidays of a year (`src/Scout.Plugin.Holidays/HolidaysClient.cs:34`).
  - `get_long_weekends` (`src/Scout.Plugin.Holidays/HolidaysPlugin.cs:162`): long weekends of the year (`src/Scout.Plugin.Holidays/HolidaysClient.cs:42`).
- **Configuration keys:** section `Scout:Plugins:Holidays` (`src/Scout.Plugin.Holidays/HolidaysServiceCollectionExtensions.cs:23`), keys `Enabled` (`src/Scout.Plugin.Holidays/HolidaysOptions.cs:15`), `DefaultCountry` (`src/Scout.Plugin.Holidays/HolidaysOptions.cs:20`), `TimeZone` (`src/Scout.Plugin.Holidays/HolidaysOptions.cs:27`). Names only; values live in the operator's configuration.
- **Data source:** HTTPS GET to Nager.Date (`src/Scout.Plugin.Holidays/HolidaysServiceCollectionExtensions.cs:25`).

## Write behaviour

Every function is read-only. No function writes to an external system or to the host filesystem outside Scout's own store.

## Invariants

- [[pages/invariants/no-executive-writes]]: no write method exists; every function is a read.
- [[pages/invariants/pluggable-boundaries]]: a self-gating `AddHolidaysPlugin` extension contributes one `KernelPlugin` singleton that `src/Scout.Llm/LlmServiceCollectionExtensions.cs:96` adds to the shared kernel; the host still references the project directly (`src/Scout.Host.Console/Scout.Host.Console.csproj`).

## Testing footprint

`tests/Scout.Plugin.Holidays.Tests` (44 test attributes, 6 files): HolidaysClientTests, HolidaysPluginMetadataTests, HolidaysPluginTests, HolidaysServiceCollectionExtensionsTests. The metadata tests pin the exact function names and require descriptions; the extension tests cover gating and registration.

## Related

Direct project references: [[pages/services/plugin-common]]. Referenced by [[pages/services/host-console]]. Catalogue: [[pages/architecture/plugin-catalogue]]. Map: [[pages/architecture/project-map]].
