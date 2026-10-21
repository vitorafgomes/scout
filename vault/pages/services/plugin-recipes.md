---
title: "Plugin.Recipes"
version: "1.0"
date: 2026-10-21
changes: "Founded from the code on 2026-10-21"
page_type: service
status: active
description: "Recipe search, detail and a random suggestion from TheMealDB."
source: ["src/Scout.Plugin.Recipes/", "tests/Scout.Plugin.Recipes.Tests/"]
reliability: high
---

# Plugin.Recipes

> Recipe search, detail and a random suggestion from TheMealDB.

## To change here, read

- [[pages/invariants/no-executive-writes]]
- [[pages/invariants/pluggable-boundaries]]
- [[pages/services/plugin-common]]
- [[pages/conventions/testing]]

## Public contract

- **Entry point:** `AddRecipesPlugin(IServiceCollection, IConfiguration)` at `src/Scout.Plugin.Recipes/RecipesServiceCollectionExtensions.cs:32`; the plugin is built with `KernelPluginFactory.CreateFromObject` at `src/Scout.Plugin.Recipes/RecipesServiceCollectionExtensions.cs:58` under the name `Recipes`; the host calls it at `src/Scout.Host.Console/HostComposition.cs:106`.
- **Gate:** Enabled read at `src/Scout.Plugin.Recipes/RecipesServiceCollectionExtensions.cs:42` (default true).
- **Model tools:**
  - `find_recipes_by_ingredient` (`src/Scout.Plugin.Recipes/RecipesPlugin.cs:46`): meals filtered by ingredient (`src/Scout.Plugin.Recipes/RecipesClient.cs:26`).
  - `get_recipe` (`src/Scout.Plugin.Recipes/RecipesPlugin.cs:78`): full recipe by name (`src/Scout.Plugin.Recipes/RecipesClient.cs:61`).
  - `suggest_random_recipe` (`src/Scout.Plugin.Recipes/RecipesPlugin.cs:110`): a random meal (`src/Scout.Plugin.Recipes/RecipesClient.cs:61`).
- **Configuration keys:** section `Scout:Plugins:Recipes` (`src/Scout.Plugin.Recipes/RecipesServiceCollectionExtensions.cs:23`), keys `Enabled` (`src/Scout.Plugin.Recipes/RecipesOptions.cs:14`). Names only; values live in the operator's configuration.
- **Data source:** HTTPS GET to TheMealDB (`src/Scout.Plugin.Recipes/RecipesServiceCollectionExtensions.cs:27`).

## Write behaviour

Every function is read-only. No function writes to an external system or to the host filesystem outside Scout's own store.

## Invariants

- [[pages/invariants/no-executive-writes]]: no write method exists; every function is a read.
- [[pages/invariants/pluggable-boundaries]]: a self-gating `AddRecipesPlugin` extension contributes one `KernelPlugin` singleton that `src/Scout.Llm/LlmServiceCollectionExtensions.cs:96` adds to the shared kernel; the host still references the project directly (`src/Scout.Host.Console/Scout.Host.Console.csproj`).

## Testing footprint

`tests/Scout.Plugin.Recipes.Tests` (43 test attributes, 6 files): RecipesClientTests, RecipesPluginMetadataTests, RecipesPluginTests, RecipesServiceCollectionExtensionsTests. The metadata tests pin the exact function names and require descriptions; the extension tests cover gating and registration.

## Related

Direct project references: [[pages/services/plugin-common]]. Referenced by [[pages/services/host-console]]. Catalogue: [[pages/architecture/plugin-catalogue]]. Map: [[pages/architecture/project-map]].
