---
title: "Plugin.Weather"
version: "1.0"
date: 2026-10-21
changes: "Founded from the code on 2026-10-21"
page_type: service
status: active
description: "Current weather, forecast and air quality from the Open-Meteo public APIs."
source: ["src/Scout.Plugin.Weather/", "tests/Scout.Plugin.Weather.Tests/"]
reliability: high
---

# Plugin.Weather

> Current weather, forecast and air quality from the Open-Meteo public APIs.

## To change here, read

- [[pages/invariants/no-executive-writes]]
- [[pages/invariants/pluggable-boundaries]]
- [[pages/services/plugin-common]]
- [[pages/conventions/testing]]

## Public contract

- **Entry point:** `AddWeatherPlugin(IServiceCollection, IConfiguration)` at `src/Scout.Plugin.Weather/WeatherServiceCollectionExtensions.cs:27`; the plugin is built with `KernelPluginFactory.CreateFromObject` at `src/Scout.Plugin.Weather/WeatherServiceCollectionExtensions.cs:53` under the name `Weather`; the host calls it at `src/Scout.Host.Console/HostComposition.cs:89`.
- **Gate:** Enabled read at `src/Scout.Plugin.Weather/WeatherServiceCollectionExtensions.cs:37` (default true).
- **Model tools:**
  - `get_current_weather` (`src/Scout.Plugin.Weather/WeatherPlugin.cs:48`): geocodes the place then reads current conditions (`src/Scout.Plugin.Weather/WeatherClient.cs:28,47`).
  - `get_forecast` (`src/Scout.Plugin.Weather/WeatherPlugin.cs:88`): daily forecast for a parsed date range (`src/Scout.Plugin.Weather/WeatherClient.cs:71`).
  - `get_air_quality` (`src/Scout.Plugin.Weather/WeatherPlugin.cs:146`): air quality index for a place (`src/Scout.Plugin.Weather/WeatherClient.cs:109`).
- **Configuration keys:** section `Scout:Plugins:Weather` (`src/Scout.Plugin.Weather/WeatherServiceCollectionExtensions.cs:22`), keys `Enabled` (`src/Scout.Plugin.Weather/WeatherOptions.cs:14`), `DefaultCity` (`src/Scout.Plugin.Weather/WeatherOptions.cs:20`), `TimeZone` (`src/Scout.Plugin.Weather/WeatherOptions.cs:26`). Names only; values live in the operator's configuration.
- **Data source:** HTTPS GET to three Open-Meteo hosts (`src/Scout.Plugin.Weather/WeatherClient.cs:19,20,21`).

## Write behaviour

Every function is read-only. No function writes to an external system or to the host filesystem outside Scout's own store.

## Invariants

- [[pages/invariants/no-executive-writes]]: no write method exists; every function is a read.
- [[pages/invariants/pluggable-boundaries]]: a self-gating `AddWeatherPlugin` extension contributes one `KernelPlugin` singleton that `src/Scout.Llm/LlmServiceCollectionExtensions.cs:96` adds to the shared kernel; the host still references the project directly (`src/Scout.Host.Console/Scout.Host.Console.csproj`).

## Testing footprint

`tests/Scout.Plugin.Weather.Tests` (43 test attributes, 7 files): WeatherClientTests, WeatherPluginMetadataTests, WeatherPluginTests, WeatherServiceCollectionExtensionsTests. The metadata tests pin the exact function names and require descriptions; the extension tests cover gating and registration.

## Related

Direct project references: [[pages/services/plugin-common]]. Referenced by [[pages/services/host-console]]. Catalogue: [[pages/architecture/plugin-catalogue]]. Map: [[pages/architecture/project-map]].
