---
title: "Plugin.Nutrition"
version: "1.0"
date: 2026-10-21
changes: "Founded from the code on 2026-10-21"
page_type: service
status: active
description: "Food product search and nutrition facts from the Open Food Facts public API."
source: ["src/Scout.Plugin.Nutrition/", "tests/Scout.Plugin.Nutrition.Tests/"]
reliability: high
---

# Plugin.Nutrition

> Food product search and nutrition facts from the Open Food Facts public API.

## To change here, read

- [[pages/invariants/no-executive-writes]]
- [[pages/invariants/pluggable-boundaries]]
- [[pages/services/plugin-common]]
- [[pages/conventions/testing]]

## Public contract

- **Entry point:** `AddNutritionPlugin(IServiceCollection, IConfiguration)` at `src/Scout.Plugin.Nutrition/NutritionServiceCollectionExtensions.cs:28`; the plugin is built with `KernelPluginFactory.CreateFromObject` at `src/Scout.Plugin.Nutrition/NutritionServiceCollectionExtensions.cs:54` under the name `Nutrition`; the host calls it at `src/Scout.Host.Console/HostComposition.cs:113`.
- **Gate:** Enabled read at `src/Scout.Plugin.Nutrition/NutritionServiceCollectionExtensions.cs:38` (default true).
- **Model tools:**
  - `find_food_product` (`src/Scout.Plugin.Nutrition/NutritionPlugin.cs:42`): product search (`src/Scout.Plugin.Nutrition/NutritionClient.cs:34`).
  - `get_nutrition_facts` (`src/Scout.Plugin.Nutrition/NutritionPlugin.cs:78`): nutrition facts for a product (`src/Scout.Plugin.Nutrition/NutritionClient.cs:47`).
  - `get_nutrition_by_barcode` (`src/Scout.Plugin.Nutrition/NutritionPlugin.cs:110`): nutrition facts by barcode (`src/Scout.Plugin.Nutrition/NutritionClient.cs:47`).
- **Configuration keys:** section `Scout:Plugins:Nutrition` (`src/Scout.Plugin.Nutrition/NutritionServiceCollectionExtensions.cs:21`), keys `Enabled` (`src/Scout.Plugin.Nutrition/NutritionOptions.cs:15`). Names only; values live in the operator's configuration.
- **Data source:** HTTPS GET to Open Food Facts (`src/Scout.Plugin.Nutrition/NutritionServiceCollectionExtensions.cs:23`).

## Write behaviour

Every function is read-only. No function writes to an external system or to the host filesystem outside Scout's own store.

## Invariants

- [[pages/invariants/no-executive-writes]]: no write method exists; every function is a read.
- [[pages/invariants/pluggable-boundaries]]: a self-gating `AddNutritionPlugin` extension contributes one `KernelPlugin` singleton that `src/Scout.Llm/LlmServiceCollectionExtensions.cs:96` adds to the shared kernel; the host still references the project directly (`src/Scout.Host.Console/Scout.Host.Console.csproj`).

## Testing footprint

`tests/Scout.Plugin.Nutrition.Tests` (44 test attributes, 6 files): NutritionClientTests, NutritionPluginMetadataTests, NutritionPluginTests, NutritionServiceCollectionExtensionsTests. The metadata tests pin the exact function names and require descriptions; the extension tests cover gating and registration.

## Related

Direct project references: [[pages/services/plugin-common]]. Referenced by [[pages/services/host-console]]. Catalogue: [[pages/architecture/plugin-catalogue]]. Map: [[pages/architecture/project-map]].
