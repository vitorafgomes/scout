---
title: "Plugin.SelfInfo"
version: "1.0"
date: 2026-10-21
changes: "Founded from the code on 2026-10-21"
page_type: service
status: active
description: "Opt-in diagnostics about the Scout host, with output tiered by caller identity."
source: ["src/Scout.Plugin.SelfInfo/", "tests/Scout.Plugin.SelfInfo.Tests/"]
reliability: high
---

# Plugin.SelfInfo

> Opt-in diagnostics about the Scout host, with output tiered by caller identity.

## To change here, read

- [[pages/invariants/no-executive-writes]]
- [[pages/invariants/pluggable-boundaries]]
- [[pages/architecture/plugin-catalogue]]
- [[pages/conventions/testing]]

## Public contract

- **Entry point:** `AddSelfInfoPlugin(IServiceCollection, IConfiguration)` at `src/Scout.Plugin.SelfInfo/SelfInfoServiceCollectionExtensions.cs:28`; the plugin is built with `KernelPluginFactory.CreateFromObject` at `src/Scout.Plugin.SelfInfo/SelfInfoServiceCollectionExtensions.cs:49` under the name `SelfInfo`; the host calls it at `src/Scout.Host.Console/HostComposition.cs:121`.
- **Gate:** Enabled read at `src/Scout.Plugin.SelfInfo/SelfInfoServiceCollectionExtensions.cs:43` (default FALSE: opt-in).
- **Model tools:**
  - `where_installed` (`src/Scout.Plugin.SelfInfo/SelfInfoPlugin.cs:64`): install kind and coarse free space; elevated callers also see real paths.
  - `system_info` (`src/Scout.Plugin.SelfInfo/SelfInfoPlugin.cs:109`): OS family, architecture, .NET major, CPU count, zone, culture.
  - `resource_usage` (`src/Scout.Plugin.SelfInfo/SelfInfoPlugin.cs:150`): qualitative uptime and memory buckets.
  - `active_configuration` (`src/Scout.Plugin.SelfInfo/SelfInfoPlugin.cs:190`): LLM provider and model names and which chat adapters are configured, by name (`src/Scout.Plugin.SelfInfo/SelfInfoPlugin.cs:200,422,427,432`).
  - `worker_status` (`src/Scout.Plugin.SelfInfo/SelfInfoPlugin.cs:236`): running state, hosting environment, Scout version.
- **Configuration keys:** section `Scout:Plugins:SelfInfo` (`src/Scout.Plugin.SelfInfo/SelfInfoServiceCollectionExtensions.cs:22`), keys `Enabled` (`src/Scout.Plugin.SelfInfo/SelfInfoOptions.cs:16`), `AdminUserIds` (`src/Scout.Plugin.SelfInfo/SelfInfoOptions.cs:24`), `DmOnly` (`src/Scout.Plugin.SelfInfo/SelfInfoOptions.cs:32`). Names only; values live in the operator's configuration.
- **Data source:** none: local .NET and OS APIs plus a whitelist of non-secret configuration keys (`Scout:Llm:Provider`, `Scout:Llm:Ollama:Chat:Model`, adapter credential keys tested for presence only, `Scout:Projects:Items`; `src/Scout.Plugin.SelfInfo/SelfInfoPlugin.cs:200,201,422-446`). It opens no connection.

## Write behaviour

Every function is read-only. No function writes to an external system or to the host filesystem outside Scout's own store.

## Disclosure tier

Output is public-safe by default; a caller whose `UserId` is in `AdminUserIds` (and in a DM when `DmOnly` is set) also gets install paths, machine name and exact versions. The tier comes from the ambient `IScoutRequestContext` (`src/Scout.Plugin.SelfInfo/SelfInfoPlugin.cs:39,88`, [[pages/services/contracts]]), gates disclosure only, and never authorises a write. Design background: [[pages/architecture/selfinfo-plugin-survey]].

## Invariants

- [[pages/invariants/no-executive-writes]]: no write method exists; every function is a read.
- [[pages/invariants/pluggable-boundaries]]: a self-gating `AddSelfInfoPlugin` extension contributes one `KernelPlugin` singleton that `src/Scout.Llm/LlmServiceCollectionExtensions.cs:96` adds to the shared kernel; the host still references the project directly (`src/Scout.Host.Console/Scout.Host.Console.csproj`).

## Testing footprint

`tests/Scout.Plugin.SelfInfo.Tests` (24 test attributes, 5 files): SelfInfoDisclosureTierTests, SelfInfoPluginMetadataTests, SelfInfoPluginTests, SelfInfoServiceCollectionExtensionsTests. The metadata tests pin the exact function names and require descriptions; the extension tests cover gating and registration.

## Related

Direct project references: [[pages/services/contracts]]. Referenced by [[pages/services/host-console]]. Catalogue: [[pages/architecture/plugin-catalogue]]. Map: [[pages/architecture/project-map]].
