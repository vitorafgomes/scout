---
title: "Plugin.WebSearch"
version: "1.0"
date: 2026-10-21
changes: "Founded from the code on 2026-10-21"
page_type: service
status: active
description: "Web search through an optional SearXNG instance, DuckDuckGo instant answers, and readable-text page fetch."
source: ["src/Scout.Plugin.WebSearch/", "tests/Scout.Plugin.WebSearch.Tests/"]
reliability: high
---

# Plugin.WebSearch

> Web search through an optional SearXNG instance, DuckDuckGo instant answers, and readable-text page fetch.

## To change here, read

- [[pages/invariants/no-executive-writes]]
- [[pages/invariants/pluggable-boundaries]]
- [[pages/services/plugin-common]]
- [[pages/conventions/testing]]

## Public contract

- **Entry point:** `AddWebSearchPlugin(IServiceCollection, IConfiguration)` at `src/Scout.Plugin.WebSearch/WebSearchServiceCollectionExtensions.cs:31`; the plugin is built with `KernelPluginFactory.CreateFromObject` at `src/Scout.Plugin.WebSearch/WebSearchServiceCollectionExtensions.cs:57` under the name `WebSearch`; the host calls it at `src/Scout.Host.Console/HostComposition.cs:104`.
- **Gate:** Enabled read at `src/Scout.Plugin.WebSearch/WebSearchServiceCollectionExtensions.cs:41` (default true).
- **Model tools:**
  - `search_web` (`src/Scout.Plugin.WebSearch/WebSearchPlugin.cs:48`): SearXNG JSON search (`src/Scout.Plugin.WebSearch/WebSearchClient.cs:48`); removed from the plugin when `SearxUrl` is blank (`src/Scout.Plugin.WebSearch/WebSearchServiceCollectionExtensions.cs:67`).
  - `get_instant_answer` (`src/Scout.Plugin.WebSearch/WebSearchPlugin.cs:86`): DuckDuckGo instant answer (`src/Scout.Plugin.WebSearch/WebSearchClient.cs:58`).
  - `fetch_page` (`src/Scout.Plugin.WebSearch/WebSearchPlugin.cs:121`): GET of a model-supplied http/https URL, HTML stripped, capped at 4000 characters (`src/Scout.Plugin.WebSearch/WebSearchPlugin.cs:25`) and fenced as untrusted.
- **Configuration keys:** section `Scout:Plugins:WebSearch` (`src/Scout.Plugin.WebSearch/WebSearchServiceCollectionExtensions.cs:24`), keys `Enabled` (`src/Scout.Plugin.WebSearch/WebSearchOptions.cs:17`), `SearxUrl` (`src/Scout.Plugin.WebSearch/WebSearchOptions.cs:24`). Names only; values live in the operator's configuration.
- **Data source:** HTTPS GET to the operator's SearXNG URL and to DuckDuckGo (`src/Scout.Plugin.WebSearch/WebSearchClient.cs:23`), and, in `fetch_page`, to any http or https URL the model supplies (`src/Scout.Plugin.WebSearch/WebSearchClient.cs:66`).

## Write behaviour

Every function is read-only (GET only). No function writes to an external system or to the host filesystem outside Scout's own store.

## Attention: model-chosen URL

`fetch_page` accepts any absolute http or https URL (`src/Scout.Plugin.WebSearch/WebSearchPlugin.cs:178,191`). I found no host allow-list and no block on loopback, private or link-local addresses in this project, so a prompt-injected model could make the host GET internal addresses. It is GET only and the body is capped, so it is a read and disclosure risk, not a write.

## Invariants

- [[pages/invariants/no-executive-writes]]: no write method exists; every function is a read.
- [[pages/invariants/pluggable-boundaries]]: a self-gating `AddWebSearchPlugin` extension contributes one `KernelPlugin` singleton that `src/Scout.Llm/LlmServiceCollectionExtensions.cs:96` adds to the shared kernel; the host still references the project directly (`src/Scout.Host.Console/Scout.Host.Console.csproj`).

## Testing footprint

`tests/Scout.Plugin.WebSearch.Tests` (55 test attributes, 6 files): WebSearchClientTests, WebSearchPluginMetadataTests, WebSearchPluginTests, WebSearchServiceCollectionExtensionsTests. The metadata tests pin the exact function names and require descriptions; the extension tests cover gating and registration.

## Related

Direct project references: [[pages/services/plugin-common]]. Referenced by [[pages/services/host-console]]. Catalogue: [[pages/architecture/plugin-catalogue]]. Map: [[pages/architecture/project-map]].
