---
title: "Plugin.Ledger"
version: "1.0"
date: 2026-10-21
changes: "Founded from the code on 2026-10-21"
page_type: service
status: active
description: "A personal income and expense ledger kept per year, with a month summary."
source: ["src/Scout.Plugin.Ledger/", "tests/Scout.Plugin.Ledger.Tests/"]
reliability: high
---

# Plugin.Ledger

> A personal income and expense ledger kept per year, with a month summary.

## To change here, read

- [[pages/invariants/no-executive-writes]]
- [[pages/invariants/pluggable-boundaries]]
- [[pages/services/plugin-common]]
- [[pages/conventions/testing]]

## Public contract

- **Entry point:** `AddLedgerPlugin(IServiceCollection, IConfiguration)` at `src/Scout.Plugin.Ledger/LedgerServiceCollectionExtensions.cs:25`; the plugin is built with `KernelPluginFactory.CreateFromObject` at `src/Scout.Plugin.Ledger/LedgerServiceCollectionExtensions.cs:43` under the name `Ledger`; the host calls it at `src/Scout.Host.Console/HostComposition.cs:115`.
- **Gate:** Enabled read at `src/Scout.Plugin.Ledger/LedgerServiceCollectionExtensions.cs:35` (default true).
- **Model tools:**
  - `record_expense` (`src/Scout.Plugin.Ledger/LedgerPlugin.cs:78`): appends an expense entry.
  - `record_income` (`src/Scout.Plugin.Ledger/LedgerPlugin.cs:138`): appends an income entry.
  - `show_month_summary` (`src/Scout.Plugin.Ledger/LedgerPlugin.cs:193`): totals for a month.
  - `list_recent_entries` (`src/Scout.Plugin.Ledger/LedgerPlugin.cs:255`): newest entries first.
- **Configuration keys:** section `Scout:Plugins:Ledger` (`src/Scout.Plugin.Ledger/LedgerServiceCollectionExtensions.cs:20`), keys `Enabled` (`src/Scout.Plugin.Ledger/LedgerOptions.cs:13`), `DefaultCurrency` (`src/Scout.Plugin.Ledger/LedgerOptions.cs:20`), `TimeZone` (`src/Scout.Plugin.Ledger/LedgerOptions.cs:26`), `Scout:Plugins:LocalStore:RootDirectory` (`src/Scout.Plugin.Common/ScoutLocalStoreOptions.cs:11`). Names only; values live in the operator's configuration.
- **Data source:** none: Scout's own local store only. Entries are bookkeeping text; nothing is sent to a bank or accounting system.

## Write behaviour

Every function is descriptive write, Scout-local only. No function writes to an external system or to the host filesystem outside Scout's own store.

## State

Collection `ledger-{year}` (`src/Scout.Plugin.Ledger/LedgerPlugin.cs:501`) in the shared `IScoutLocalStore`, one JSON file per collection under `{LocalApplicationData}/Scout/plugin-data` unless `Scout:Plugins:LocalStore:RootDirectory` overrides it (`src/Scout.Plugin.Common/JsonFileScoutLocalStore.cs:30,110`). This is Scout's own storage on the local machine, written atomically (`src/Scout.Plugin.Common/JsonFileScoutLocalStore.cs:103`); see [[pages/services/plugin-common]].

## Invariants

- [[pages/invariants/no-executive-writes]]: writes go only through `IScoutLocalStore`, Scout's own storage; nothing external is mutated.
- [[pages/invariants/pluggable-boundaries]]: a self-gating `AddLedgerPlugin` extension contributes one `KernelPlugin` singleton that `src/Scout.Llm/LlmServiceCollectionExtensions.cs:96` adds to the shared kernel; the host still references the project directly (`src/Scout.Host.Console/Scout.Host.Console.csproj`).

## Testing footprint

`tests/Scout.Plugin.Ledger.Tests` (50 test attributes, 5 files): LedgerPluginMetadataTests, LedgerPluginTests, LedgerServiceCollectionExtensionsTests. The metadata tests pin the exact function names and require descriptions; the extension tests cover gating and registration.

## Related

Direct project references: [[pages/services/plugin-common]]. Referenced by [[pages/services/host-console]]. Catalogue: [[pages/architecture/plugin-catalogue]]. Map: [[pages/architecture/project-map]].
