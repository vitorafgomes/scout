---
title: "Plugin.WhatToWatch"
version: "1.0"
date: 2026-10-21
changes: "Founded from the code on 2026-10-21"
page_type: service
status: active
description: "TV show search, next episode, genre recommendations (TVMaze) and streaming availability (TMDB, optional key)."
source: ["src/Scout.Plugin.WhatToWatch/", "tests/Scout.Plugin.WhatToWatch.Tests/"]
reliability: high
---

# Plugin.WhatToWatch

> TV show search, next episode, genre recommendations (TVMaze) and streaming availability (TMDB, optional key).

## To change here, read

- [[pages/invariants/no-executive-writes]]
- [[pages/invariants/pluggable-boundaries]]
- [[pages/services/plugin-common]]
- [[pages/conventions/testing]]

## Public contract

- **Entry point:** `AddWhatToWatchPlugin(IServiceCollection, IConfiguration)` at `src/Scout.Plugin.WhatToWatch/WhatToWatchServiceCollectionExtensions.cs:32`; the plugin is built with `KernelPluginFactory.CreateFromObject` at `src/Scout.Plugin.WhatToWatch/WhatToWatchServiceCollectionExtensions.cs:76` under the name `WhatToWatch`; the host calls it at `src/Scout.Host.Console/HostComposition.cs:114`.
- **Gate:** Enabled read at `src/Scout.Plugin.WhatToWatch/WhatToWatchServiceCollectionExtensions.cs:42` (default true).
- **Model tools:**
  - `search_show` (`src/Scout.Plugin.WhatToWatch/WhatToWatchPlugin.cs:70`): show search (`src/Scout.Plugin.WhatToWatch/TvMazeClient.cs:25`).
  - `get_next_episode` (`src/Scout.Plugin.WhatToWatch/WhatToWatchPlugin.cs:102`): next episode of a show (`src/Scout.Plugin.WhatToWatch/TvMazeClient.cs:37`).
  - `recommend_shows` (`src/Scout.Plugin.WhatToWatch/WhatToWatchPlugin.cs:138`): shows by genre (`src/Scout.Plugin.WhatToWatch/TvMazeClient.cs:53`).
  - `where_to_watch` (`src/Scout.Plugin.WhatToWatch/WhatToWatchPlugin.cs:171`): watch providers by country (`src/Scout.Plugin.WhatToWatch/TmdbClient.cs:44`); answers with a configuration hint when no TMDB key is set (`src/Scout.Plugin.WhatToWatch/WhatToWatchPlugin.cs:189`).
- **Configuration keys:** section `Scout:Plugins:WhatToWatch` (`src/Scout.Plugin.WhatToWatch/WhatToWatchServiceCollectionExtensions.cs:23`), keys `Enabled` (`src/Scout.Plugin.WhatToWatch/WhatToWatchOptions.cs:18`), `TmdbApiKey` (`src/Scout.Plugin.WhatToWatch/WhatToWatchOptions.cs:25`), `DefaultCountry` (`src/Scout.Plugin.WhatToWatch/WhatToWatchOptions.cs:31`), `TimeZone` (`src/Scout.Plugin.WhatToWatch/WhatToWatchOptions.cs:37`), `MaxResults` (`src/Scout.Plugin.WhatToWatch/WhatToWatchOptions.cs:43`). Names only; values live in the operator's configuration.
- **Data source:** HTTPS GET to TVMaze and, only when `TmdbApiKey` is set (`src/Scout.Plugin.WhatToWatch/WhatToWatchServiceCollectionExtensions.cs:60`), to TMDB (`:25,26`).

## Write behaviour

Every function is read-only. No function writes to an external system or to the host filesystem outside Scout's own store.

## Invariants

- [[pages/invariants/no-executive-writes]]: no write method exists; every function is a read.
- [[pages/invariants/pluggable-boundaries]]: a self-gating `AddWhatToWatchPlugin` extension contributes one `KernelPlugin` singleton that `src/Scout.Llm/LlmServiceCollectionExtensions.cs:96` adds to the shared kernel; the host still references the project directly (`src/Scout.Host.Console/Scout.Host.Console.csproj`).

## Testing footprint

`tests/Scout.Plugin.WhatToWatch.Tests` (58 test attributes, 6 files): WhatToWatchClientTests, WhatToWatchPluginMetadataTests, WhatToWatchPluginTests, WhatToWatchServiceCollectionExtensionsTests. The metadata tests pin the exact function names and require descriptions; the extension tests cover gating and registration.

## Related

Direct project references: [[pages/services/plugin-common]]. Referenced by [[pages/services/host-console]]. Catalogue: [[pages/architecture/plugin-catalogue]]. Map: [[pages/architecture/project-map]].
