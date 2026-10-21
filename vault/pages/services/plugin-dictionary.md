---
title: "Plugin.Dictionary"
version: "1.0"
date: 2026-10-21
changes: "Founded from the code on 2026-10-21"
page_type: service
status: active
description: "Word definitions, synonyms and spelling suggestions from two free public APIs."
source: ["src/Scout.Plugin.Dictionary/", "tests/Scout.Plugin.Dictionary.Tests/"]
reliability: high
---

# Plugin.Dictionary

> Word definitions, synonyms and spelling suggestions from two free public APIs.

## To change here, read

- [[pages/invariants/no-executive-writes]]
- [[pages/invariants/pluggable-boundaries]]
- [[pages/services/plugin-common]]
- [[pages/conventions/testing]]

## Public contract

- **Entry point:** `AddDictionaryPlugin(IServiceCollection, IConfiguration)` at `src/Scout.Plugin.Dictionary/DictionaryServiceCollectionExtensions.cs:30`; the plugin is built with `KernelPluginFactory.CreateFromObject` at `src/Scout.Plugin.Dictionary/DictionaryServiceCollectionExtensions.cs:64` under the name `Dictionary`; the host calls it at `src/Scout.Host.Console/HostComposition.cs:105`.
- **Gate:** Enabled read at `src/Scout.Plugin.Dictionary/DictionaryServiceCollectionExtensions.cs:40` (default true).
- **Model tools:**
  - `define_word` (`src/Scout.Plugin.Dictionary/DictionaryPlugin.cs:41`): definitions via dictionaryapi.dev (`src/Scout.Plugin.Dictionary/DefinitionsClient.cs:30`).
  - `get_synonyms` (`src/Scout.Plugin.Dictionary/DictionaryPlugin.cs:72`): synonyms via Datamuse (`src/Scout.Plugin.Dictionary/DatamuseClient.cs:42`).
  - `check_spelling` (`src/Scout.Plugin.Dictionary/DictionaryPlugin.cs:100`): spelling suggestions via Datamuse (`src/Scout.Plugin.Dictionary/DatamuseClient.cs:42`).
- **Configuration keys:** section `Scout:Plugins:Dictionary` (`src/Scout.Plugin.Dictionary/DictionaryServiceCollectionExtensions.cs:22`), keys `Enabled` (`src/Scout.Plugin.Dictionary/DictionaryOptions.cs:16`). Names only; values live in the operator's configuration.
- **Data source:** HTTPS GET to two constant base addresses (`src/Scout.Plugin.Dictionary/DictionaryServiceCollectionExtensions.cs:24,25`).

## Write behaviour

Every function is read-only. No function writes to an external system or to the host filesystem outside Scout's own store.

## Invariants

- [[pages/invariants/no-executive-writes]]: no write method exists; every function is a read.
- [[pages/invariants/pluggable-boundaries]]: a self-gating `AddDictionaryPlugin` extension contributes one `KernelPlugin` singleton that `src/Scout.Llm/LlmServiceCollectionExtensions.cs:96` adds to the shared kernel; the host still references the project directly (`src/Scout.Host.Console/Scout.Host.Console.csproj`).

## Testing footprint

`tests/Scout.Plugin.Dictionary.Tests` (50 test attributes, 6 files): DictionaryClientTests, DictionaryPluginMetadataTests, DictionaryPluginTests, DictionaryServiceCollectionExtensionsTests. The metadata tests pin the exact function names and require descriptions; the extension tests cover gating and registration.

## Related

Direct project references: [[pages/services/plugin-common]]. Referenced by [[pages/services/host-console]]. Catalogue: [[pages/architecture/plugin-catalogue]]. Map: [[pages/architecture/project-map]].
