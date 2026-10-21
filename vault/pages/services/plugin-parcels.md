---
title: "Plugin.Parcels"
version: "1.0"
date: 2026-10-21
changes: "Founded from the code on 2026-10-21"
page_type: service
status: active
description: "Parcel tracking through Ship24 plus a local watch list of tracking numbers."
source: ["src/Scout.Plugin.Parcels/", "tests/Scout.Plugin.Parcels.Tests/"]
reliability: high
---

# Plugin.Parcels

> Parcel tracking through Ship24 plus a local watch list of tracking numbers.

## To change here, read

- [[pages/invariants/no-executive-writes]]
- [[pages/invariants/pluggable-boundaries]]
- [[pages/services/plugin-common]]
- [[pages/conventions/testing]]

## Public contract

- **Entry point:** `AddParcelsPlugin(IServiceCollection, IConfiguration)` at `src/Scout.Plugin.Parcels/ParcelsServiceCollectionExtensions.cs:31`; the plugin is built with `KernelPluginFactory.CreateFromObject` at `src/Scout.Plugin.Parcels/ParcelsServiceCollectionExtensions.cs:60` under the name `Parcels`; the host calls it at `src/Scout.Host.Console/HostComposition.cs:116`.
- **Gate:** ApiKey read at `src/Scout.Plugin.Parcels/ParcelsServiceCollectionExtensions.cs:41` (blank means the plugin is not registered).
- **Model tools:**
  - `track_package` (`src/Scout.Plugin.Parcels/ParcelsPlugin.cs:53`): looks up status and recent movements by tracking number (`src/Scout.Plugin.Parcels/ParcelsClient.cs:28`).
  - `watch_package` (`src/Scout.Plugin.Parcels/ParcelsPlugin.cs:83`): adds a number and optional label to the local watch list; no carrier or provider call.
  - `list_watched_packages` (`src/Scout.Plugin.Parcels/ParcelsPlugin.cs:133`): lists the local watch list.
  - `unwatch_package` (`src/Scout.Plugin.Parcels/ParcelsPlugin.cs:158`): removes a number from the local watch list.
- **Configuration keys:** section `Scout:Plugins:Parcels` (`src/Scout.Plugin.Parcels/ParcelsServiceCollectionExtensions.cs:24`), keys `ApiKey` (`src/Scout.Plugin.Parcels/ParcelsOptions.cs:15`), `TimeZone` (`src/Scout.Plugin.Parcels/ParcelsOptions.cs:22`), `Scout:Plugins:LocalStore:RootDirectory` (`src/Scout.Plugin.Common/ScoutLocalStoreOptions.cs:11`). Names only; values live in the operator's configuration.
- **Data source:** HTTPS to Ship24 with a bearer key (`src/Scout.Plugin.Parcels/ParcelsServiceCollectionExtensions.cs:26`).

## Write behaviour

Every function is read-only by intent, but see the HTTP POST note below; the watch list is a descriptive write to Scout's local store. No function writes to the host filesystem outside Scout's own store.

## State

Collection `watched-packages` (`src/Scout.Plugin.Parcels/ParcelsPlugin.cs:20`) in the shared `IScoutLocalStore`, one JSON file per collection under `{LocalApplicationData}/Scout/plugin-data` unless `Scout:Plugins:LocalStore:RootDirectory` overrides it (`src/Scout.Plugin.Common/JsonFileScoutLocalStore.cs:30,110`). This is Scout's own storage on the local machine, written atomically (`src/Scout.Plugin.Common/JsonFileScoutLocalStore.cs:103`); see [[pages/services/plugin-common]].

## Attention: the one non-GET external call

`track_package` sends an HTTP POST to the Ship24 tracking-search endpoint, carrying the tracking number (`src/Scout.Plugin.Parcels/ParcelsClient.cs:28`). It is the only non-GET request in any plugin. The client comment states the endpoint returns results without creating or subscribing a tracker on the provider (`src/Scout.Plugin.Parcels/ParcelsClient.cs:13,21`), so no external state is meant to change; that is the provider's documented behaviour, not something the code enforces. The tests use a stub handler (`tests/Scout.Plugin.Parcels.Tests/ParcelsClientTests.cs`).

## Invariants

- [[pages/invariants/no-executive-writes]]: the watch list is Scout-local; the only external request is the Ship24 search POST described above, which the provider documents as non-mutating.
- [[pages/invariants/pluggable-boundaries]]: a self-gating `AddParcelsPlugin` extension contributes one `KernelPlugin` singleton that `src/Scout.Llm/LlmServiceCollectionExtensions.cs:96` adds to the shared kernel; the host still references the project directly (`src/Scout.Host.Console/Scout.Host.Console.csproj`).

## Testing footprint

`tests/Scout.Plugin.Parcels.Tests` (47 test attributes, 7 files): ParcelsClientTests, ParcelsPluginMetadataTests, ParcelsPluginTests, ParcelsServiceCollectionExtensionsTests. The metadata tests pin the exact function names and require descriptions; the extension tests cover gating and registration.

## Related

Direct project references: [[pages/services/plugin-common]]. Referenced by [[pages/services/host-console]]. Catalogue: [[pages/architecture/plugin-catalogue]]. Map: [[pages/architecture/project-map]].
