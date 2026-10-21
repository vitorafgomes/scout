---
title: "Plugin.News"
version: "1.0"
date: 2026-10-21
changes: "Founded from the code on 2026-10-21"
page_type: service
status: active
description: "Headlines from operator-configured RSS feeds, Hacker News search and top stories, and a local reading list."
source: ["src/Scout.Plugin.News/", "tests/Scout.Plugin.News.Tests/"]
reliability: high
---

# Plugin.News

> Headlines from operator-configured RSS feeds, Hacker News search and top stories, and a local reading list.

## To change here, read

- [[pages/invariants/no-executive-writes]]
- [[pages/invariants/pluggable-boundaries]]
- [[pages/services/plugin-common]]
- [[pages/conventions/testing]]

## Public contract

- **Entry point:** `AddNewsPlugin(IServiceCollection, IConfiguration)` at `src/Scout.Plugin.News/NewsServiceCollectionExtensions.cs:31`; the plugin is built with `KernelPluginFactory.CreateFromObject` at `src/Scout.Plugin.News/NewsServiceCollectionExtensions.cs:69` under the name `News`; the host calls it at `src/Scout.Host.Console/HostComposition.cs:103`.
- **Gate:** Enabled read at `src/Scout.Plugin.News/NewsServiceCollectionExtensions.cs:41` (default true).
- **Model tools:**
  - `get_top_headlines` (`src/Scout.Plugin.News/NewsPlugin.cs:62`): items of a configured feed (`src/Scout.Plugin.News/FeedClient.cs:23`).
  - `search_news` (`src/Scout.Plugin.News/NewsPlugin.cs:94`): Hacker News search (`src/Scout.Plugin.News/HackerNewsClient.cs:25`).
  - `get_hacker_news_top` (`src/Scout.Plugin.News/NewsPlugin.cs:125`): Hacker News front page (`src/Scout.Plugin.News/HackerNewsClient.cs:32`).
  - `save_to_reading_list` (`src/Scout.Plugin.News/NewsPlugin.cs:151`): stores a link and optional title; it does not fetch the link.
  - `get_reading_list` (`src/Scout.Plugin.News/NewsPlugin.cs:197`): lists saved links, newest first.
- **Configuration keys:** section `Scout:Plugins:News` (`src/Scout.Plugin.News/NewsServiceCollectionExtensions.cs:24`), keys `Enabled` (`src/Scout.Plugin.News/NewsOptions.cs:16`), `Feeds` (`src/Scout.Plugin.News/NewsOptions.cs:24`), `Scout:Plugins:LocalStore:RootDirectory` (`src/Scout.Plugin.Common/ScoutLocalStoreOptions.cs:11`). Names only; values live in the operator's configuration.
- **Data source:** HTTPS GET to Hacker News via Algolia (`src/Scout.Plugin.News/NewsServiceCollectionExtensions.cs:26`) and to the feed URLs the operator lists under `Feeds` (`src/Scout.Plugin.News/FeedClient.cs:23`); the model picks a feed alias, not a URL (`src/Scout.Plugin.News/NewsPlugin.cs:53`).

## Write behaviour

Every function is read-only except `save_to_reading_list`, a descriptive write to Scout's local store. No function writes to an external system or to the host filesystem outside Scout's own store.

## State

Collection `reading-list` (`src/Scout.Plugin.News/NewsPlugin.cs:22`) in the shared `IScoutLocalStore`, one JSON file per collection under `{LocalApplicationData}/Scout/plugin-data` unless `Scout:Plugins:LocalStore:RootDirectory` overrides it (`src/Scout.Plugin.Common/JsonFileScoutLocalStore.cs:30,110`). This is Scout's own storage on the local machine, written atomically (`src/Scout.Plugin.Common/JsonFileScoutLocalStore.cs:103`); see [[pages/services/plugin-common]].

## Invariants

- [[pages/invariants/no-executive-writes]]: reads feeds and Hacker News; the reading list is Scout-local storage.
- [[pages/invariants/pluggable-boundaries]]: a self-gating `AddNewsPlugin` extension contributes one `KernelPlugin` singleton that `src/Scout.Llm/LlmServiceCollectionExtensions.cs:96` adds to the shared kernel; the host still references the project directly (`src/Scout.Host.Console/Scout.Host.Console.csproj`).

## Testing footprint

`tests/Scout.Plugin.News.Tests` (58 test attributes, 6 files): NewsClientTests, NewsPluginMetadataTests, NewsPluginTests, NewsServiceCollectionExtensionsTests. The metadata tests pin the exact function names and require descriptions; the extension tests cover gating and registration.

## Related

Direct project references: [[pages/services/plugin-common]]. Referenced by [[pages/services/host-console]]. Catalogue: [[pages/architecture/plugin-catalogue]]. Map: [[pages/architecture/project-map]].
