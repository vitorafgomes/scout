---
title: "Plugin.Markets"
version: "1.0"
date: 2026-10-21
changes: "Founded from the code on 2026-10-21"
page_type: service
status: active
description: "Stock quotes from Stooq and crypto prices and trending coins from CoinGecko."
source: ["src/Scout.Plugin.Markets/", "tests/Scout.Plugin.Markets.Tests/"]
reliability: high
---

# Plugin.Markets

> Stock quotes from Stooq and crypto prices and trending coins from CoinGecko.

## To change here, read

- [[pages/invariants/no-executive-writes]]
- [[pages/invariants/pluggable-boundaries]]
- [[pages/services/plugin-common]]
- [[pages/conventions/testing]]

## Public contract

- **Entry point:** `AddMarketsPlugin(IServiceCollection, IConfiguration)` at `src/Scout.Plugin.Markets/MarketsServiceCollectionExtensions.cs:32`; the plugin is built with `KernelPluginFactory.CreateFromObject` at `src/Scout.Plugin.Markets/MarketsServiceCollectionExtensions.cs:77` under the name `Markets`; the host calls it at `src/Scout.Host.Console/HostComposition.cs:111`.
- **Gate:** Enabled read at `src/Scout.Plugin.Markets/MarketsServiceCollectionExtensions.cs:42` (default true).
- **Model tools:**
  - `get_stock_quote` (`src/Scout.Plugin.Markets/MarketsPlugin.cs:45`): CSV quote for a ticker (`src/Scout.Plugin.Markets/StooqClient.cs:27`).
  - `get_crypto_price` (`src/Scout.Plugin.Markets/MarketsPlugin.cs:75`): price of a coin in a quote currency (`src/Scout.Plugin.Markets/CoinGeckoClient.cs:64`).
  - `get_trending_crypto` (`src/Scout.Plugin.Markets/MarketsPlugin.cs:110`): trending coins (`src/Scout.Plugin.Markets/CoinGeckoClient.cs:85`).
- **Configuration keys:** section `Scout:Plugins:Markets` (`src/Scout.Plugin.Markets/MarketsServiceCollectionExtensions.cs:24`), keys `Enabled` (`src/Scout.Plugin.Markets/MarketsOptions.cs:16`), `CoinGeckoApiKey` (`src/Scout.Plugin.Markets/MarketsOptions.cs:23`). Names only; values live in the operator's configuration.
- **Data source:** HTTPS GET to Stooq and CoinGecko (`src/Scout.Plugin.Markets/MarketsServiceCollectionExtensions.cs:26,27`). `CoinGeckoApiKey` is optional and read at `:52`.

## Write behaviour

Every function is read-only. No function writes to an external system or to the host filesystem outside Scout's own store.

## Invariants

- [[pages/invariants/no-executive-writes]]: no write method exists; every function is a read.
- [[pages/invariants/pluggable-boundaries]]: a self-gating `AddMarketsPlugin` extension contributes one `KernelPlugin` singleton that `src/Scout.Llm/LlmServiceCollectionExtensions.cs:96` adds to the shared kernel; the host still references the project directly (`src/Scout.Host.Console/Scout.Host.Console.csproj`).

## Testing footprint

`tests/Scout.Plugin.Markets.Tests` (52 test attributes, 6 files): MarketsClientTests, MarketsPluginMetadataTests, MarketsPluginTests, MarketsServiceCollectionExtensionsTests. The metadata tests pin the exact function names and require descriptions; the extension tests cover gating and registration.

## Related

Direct project references: [[pages/services/plugin-common]]. Referenced by [[pages/services/host-console]]. Catalogue: [[pages/architecture/plugin-catalogue]]. Map: [[pages/architecture/project-map]].
