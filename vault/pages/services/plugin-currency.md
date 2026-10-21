---
title: "Plugin.Currency"
version: "1.0"
date: 2026-10-21
changes: "Founded from the code on 2026-10-21"
page_type: service
status: active
description: "Exchange rates, currency conversion and a rate trend from the Frankfurter public API."
source: ["src/Scout.Plugin.Currency/", "tests/Scout.Plugin.Currency.Tests/"]
reliability: high
---

# Plugin.Currency

> Exchange rates, currency conversion and a rate trend from the Frankfurter public API.

## To change here, read

- [[pages/invariants/no-executive-writes]]
- [[pages/invariants/pluggable-boundaries]]
- [[pages/architecture/plugin-catalogue]]
- [[pages/conventions/testing]]

## Public contract

- **Entry point:** `AddCurrencyPlugin(IServiceCollection, IConfiguration)` at `src/Scout.Plugin.Currency/CurrencyServiceCollectionExtensions.cs:26`; the plugin is built with `KernelPluginFactory.CreateFromObject` at `src/Scout.Plugin.Currency/CurrencyServiceCollectionExtensions.cs:50` under the name `Currency`; the host calls it at `src/Scout.Host.Console/HostComposition.cs:92`.
- **Gate:** Enabled read at `src/Scout.Plugin.Currency/CurrencyServiceCollectionExtensions.cs:36` (default true).
- **Model tools:**
  - `get_exchange_rate` (`src/Scout.Plugin.Currency/CurrencyPlugin.cs:37`): latest rate for a pair (`src/Scout.Plugin.Currency/CurrencyClient.cs:26`).
  - `convert_currency` (`src/Scout.Plugin.Currency/CurrencyPlugin.cs:70`): converts an amount using the latest rate (`src/Scout.Plugin.Currency/CurrencyClient.cs:26`).
  - `get_rate_trend` (`src/Scout.Plugin.Currency/CurrencyPlugin.cs:113`): time series for a pair (`src/Scout.Plugin.Currency/CurrencyClient.cs:51`).
- **Configuration keys:** section `Scout:Plugins:Currency` (`src/Scout.Plugin.Currency/CurrencyServiceCollectionExtensions.cs:19`), keys `Enabled` (`src/Scout.Plugin.Currency/CurrencyOptions.cs:14`). Names only; values live in the operator's configuration.
- **Data source:** HTTPS GET to the Frankfurter API; base address is a constant (`src/Scout.Plugin.Currency/CurrencyServiceCollectionExtensions.cs:21`).

## Write behaviour

Every function is read-only. No function writes to an external system or to the host filesystem outside Scout's own store.

## Invariants

- [[pages/invariants/no-executive-writes]]: no write method exists; every function is a read.
- [[pages/invariants/pluggable-boundaries]]: a self-gating `AddCurrencyPlugin` extension contributes one `KernelPlugin` singleton that `src/Scout.Llm/LlmServiceCollectionExtensions.cs:96` adds to the shared kernel; the host still references the project directly (`src/Scout.Host.Console/Scout.Host.Console.csproj`).

## Testing footprint

`tests/Scout.Plugin.Currency.Tests` (40 test attributes, 6 files): CurrencyClientTests, CurrencyPluginMetadataTests, CurrencyPluginTests, CurrencyServiceCollectionExtensionsTests. The metadata tests pin the exact function names and require descriptions; the extension tests cover gating and registration.

## Related

Direct project references: none. Referenced by [[pages/services/host-console]]. Catalogue: [[pages/architecture/plugin-catalogue]]. Map: [[pages/architecture/project-map]].
