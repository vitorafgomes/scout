---
title: "Plugin.Wikipedia"
version: "1.0"
date: 2026-10-21
changes: "Founded from the code on 2026-10-21"
page_type: service
status: active
description: "Wikipedia search, article summaries and on-this-day events through the Wikimedia API."
source: ["src/Scout.Plugin.Wikipedia/", "tests/Scout.Plugin.Wikipedia.Tests/"]
reliability: high
---

# Plugin.Wikipedia

> Wikipedia search, article summaries and on-this-day events through the Wikimedia API.

## To change here, read

- [[pages/invariants/no-executive-writes]]
- [[pages/invariants/pluggable-boundaries]]
- [[pages/architecture/plugin-catalogue]]
- [[pages/conventions/testing]]

## Public contract

- **Entry point:** `AddWikipediaPlugin(IServiceCollection, IConfiguration)` at `src/Scout.Plugin.Wikipedia/WikipediaServiceCollectionExtensions.cs:27`; the plugin is built with `KernelPluginFactory.CreateFromObject` at `src/Scout.Plugin.Wikipedia/WikipediaServiceCollectionExtensions.cs:52` under the name `Wikipedia`; the host calls it at `src/Scout.Host.Console/HostComposition.cs:91`.
- **Gate:** Enabled read at `src/Scout.Plugin.Wikipedia/WikipediaServiceCollectionExtensions.cs:37` (default true).
- **Model tools:**
  - `search_wikipedia` (`src/Scout.Plugin.Wikipedia/WikipediaPlugin.cs:34`): title search (`src/Scout.Plugin.Wikipedia/WikipediaClient.cs:32`).
  - `get_article_summary` (`src/Scout.Plugin.Wikipedia/WikipediaPlugin.cs:70`): summary of one article (`src/Scout.Plugin.Wikipedia/WikipediaClient.cs:49`).
  - `get_on_this_day` (`src/Scout.Plugin.Wikipedia/WikipediaPlugin.cs:114`): events for today's month and day (`src/Scout.Plugin.Wikipedia/WikipediaClient.cs:69`).
- **Configuration keys:** section `Scout:Plugins:Wikipedia` (`src/Scout.Plugin.Wikipedia/WikipediaServiceCollectionExtensions.cs:20`), keys `Enabled` (`src/Scout.Plugin.Wikipedia/WikipediaOptions.cs:15`). Names only; values live in the operator's configuration.
- **Data source:** HTTPS GET to the Wikimedia API (`src/Scout.Plugin.Wikipedia/WikipediaServiceCollectionExtensions.cs:22`).

## Write behaviour

Every function is read-only. No function writes to an external system or to the host filesystem outside Scout's own store.

## Invariants

- [[pages/invariants/no-executive-writes]]: no write method exists; every function is a read.
- [[pages/invariants/pluggable-boundaries]]: a self-gating `AddWikipediaPlugin` extension contributes one `KernelPlugin` singleton that `src/Scout.Llm/LlmServiceCollectionExtensions.cs:96` adds to the shared kernel; the host still references the project directly (`src/Scout.Host.Console/Scout.Host.Console.csproj`).

## Testing footprint

`tests/Scout.Plugin.Wikipedia.Tests` (39 test attributes, 6 files): WikipediaClientTests, WikipediaPluginMetadataTests, WikipediaPluginTests, WikipediaServiceCollectionExtensionsTests. The metadata tests pin the exact function names and require descriptions; the extension tests cover gating and registration.

## Related

Direct project references: none. Referenced by [[pages/services/host-console]]. Catalogue: [[pages/architecture/plugin-catalogue]]. Map: [[pages/architecture/project-map]].
