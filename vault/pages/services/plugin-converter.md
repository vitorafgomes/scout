---
title: "Plugin.Converter"
version: "1.0"
date: 2026-10-21
changes: "Founded from the code on 2026-10-21"
page_type: service
status: active
description: "Converts physical units and evaluates plain decimal arithmetic locally; no network."
source: ["src/Scout.Plugin.Converter/", "tests/Scout.Plugin.Converter.Tests/"]
reliability: high
---

# Plugin.Converter

> Converts physical units and evaluates plain decimal arithmetic locally; no network.

## To change here, read

- [[pages/invariants/no-executive-writes]]
- [[pages/invariants/pluggable-boundaries]]
- [[pages/services/plugin-common]]
- [[pages/conventions/testing]]

## Public contract

- **Entry point:** `AddConverterPlugin(IServiceCollection, IConfiguration)` at `src/Scout.Plugin.Converter/ConverterServiceCollectionExtensions.cs:25`; the plugin is built with `KernelPluginFactory.CreateFromObject` at `src/Scout.Plugin.Converter/ConverterServiceCollectionExtensions.cs:41` under the name `Converter`; the host calls it at `src/Scout.Host.Console/HostComposition.cs:102`.
- **Gate:** Enabled read at `src/Scout.Plugin.Converter/ConverterServiceCollectionExtensions.cs:35` (default true).
- **Model tools:**
  - `convert_units` (`src/Scout.Plugin.Converter/ConverterPlugin.cs:38`): converts a value between units using the in-process table (`src/Scout.Plugin.Converter/UnitConversion.cs`); currency is refused.
  - `calculate` (`src/Scout.Plugin.Converter/ConverterPlugin.cs:61`): evaluates + - * / and parentheses with a hand-written evaluator (`src/Scout.Plugin.Converter/ArithmeticEvaluator.cs`), length capped by `MaxExpressionLength`.
- **Configuration keys:** section `Scout:Plugins:Converter` (`src/Scout.Plugin.Converter/ConverterServiceCollectionExtensions.cs:20`), keys `Enabled` (`src/Scout.Plugin.Converter/ConverterOptions.cs:14`), `MaxExpressionLength` (`src/Scout.Plugin.Converter/ConverterOptions.cs:22`). Names only; values live in the operator's configuration.
- **Data source:** none: pure computation, no HttpClient is registered.

## Write behaviour

Every function is read-only (pure functions). No function writes to an external system or to the host filesystem outside Scout's own store.

## Invariants

- [[pages/invariants/no-executive-writes]]: no write method exists; every function is a read.
- [[pages/invariants/pluggable-boundaries]]: a self-gating `AddConverterPlugin` extension contributes one `KernelPlugin` singleton that `src/Scout.Llm/LlmServiceCollectionExtensions.cs:96` adds to the shared kernel; the host still references the project directly (`src/Scout.Host.Console/Scout.Host.Console.csproj`).

## Testing footprint

`tests/Scout.Plugin.Converter.Tests` (45 test attributes, 5 files): ArithmeticEvaluatorTests, ConverterPluginMetadataTests, ConverterPluginTests, ConverterServiceCollectionExtensionsTests. The metadata tests pin the exact function names and require descriptions; the extension tests cover gating and registration.

## Related

Direct project references: [[pages/services/plugin-common]]. Referenced by [[pages/services/host-console]]. Catalogue: [[pages/architecture/plugin-catalogue]]. Map: [[pages/architecture/project-map]].
