---
title: "Scout performs descriptive writes only"
version: "1.0"
date: 2026-10-21
changes: "Founded from the code on 2026-10-21"
page_type: invariant
status: active
description: "Scout may write content that a human or another agent reads and decides on; it never mutates code, infrastructure, configuration or data on the host. The code upholds this by omission, not by a gate."
enforced_by:
  - "src/Scout.Plugin.GitHub/Abstractions/IGitHubClient.cs:7"
  - "src/Scout.Plugin.GitHub/GitHubClient.cs:157"
  - "src/Scout.Plugin.GitHub/GitHubPlugin.cs:15"
verified_by:
  - "tests/Scout.Plugin.GitHub.Tests/GitHubPluginMetadataTests.cs:25"
  - "tests/Scout.Plugin.GitHub.Tests/GitHubServiceCollectionExtensionsTests.cs:24"
source:
  - "src/Scout.Plugin.GitHub/"
  - "src/Scout.Llm/"
  - "src/Scout.Skills/"
  - "src/Scout.Knowledge/"
  - "src/Scout.ControlPlane/"
  - "tests/Scout.Plugin.GitHub.Tests/"
reliability: high
---

# Scout performs descriptive writes only

> Every write Scout makes is content a person or another agent reads and decides on (a chat reply, an issue body); no code path may mutate code, infrastructure, configuration or data on the host or in a connected system.

## The gap

Nothing in code stops a plugin from exposing a write function. The kernel takes every `KernelPlugin` registered in DI (`src/Scout.Llm/LlmServiceCollectionExtensions.cs:96`) and offers all of their functions to the model with `FunctionChoiceBehavior.Auto()` (`src/Scout.Llm/ChatGateway.cs:264`, `src/Scout.Knowledge/ProjectExpertAgentFactory.cs:84`). There is no allow-list, no read-only marker on a function, and no test that fails when a write function appears. Read-only holds today because the write methods do not exist, and for no other reason.

## Why this exists

Scout answers questions in shared chat channels. A model that can call a mutating tool turns a prompt in a public channel into a change on a real system. The README states the rule as "never writes"; [[pages/decisions/0009-descriptive-vs-executive-writes]] refines it to descriptive versus executive writes.

## Scope

- **Applies to:** every `KernelPlugin` the host registers, the GitHub client, the chat adapters, and the control plane.
- **Does not cover:** Scout's own state (below), and replies posted to the chat platform, which are descriptive writes by definition.

## Where it is held

- `src/Scout.Plugin.GitHub/Abstractions/IGitHubClient.cs:7` declares four read methods and no others; the interface comment says no create, merge, push or comment operation exists by design.
- `src/Scout.Plugin.GitHub/GitHubClient.cs:140` and `:157` are the only HTTP calls, both `GetAsync`.
- `src/Scout.Plugin.GitHub/GitHubPlugin.cs:29,51,76,101` expose `list_repositories`, `list_pull_requests`, `list_issues`, `get_repository`.
- `src/Scout.Skills/SkillsPlugin.cs:27,40` (`list_skills`, `load_skill`) and `src/Scout.Knowledge/ProjectKnowledgePlugin.cs:26,41,53` (`search_project`, `find_symbol`, `list_projects`) only read.
- `src/Scout.Plugin.GitHub/GitHubServiceCollectionExtensions.cs:38` registers nothing when no token is set.

## How it is verified

- `tests/Scout.Plugin.GitHub.Tests/GitHubPluginMetadataTests.cs:25` asserts the four expected functions exist. It does not assert that no other function exists, so adding `create_issue` would not fail it.
- `tests/Scout.Plugin.GitHub.Tests/GitHubServiceCollectionExtensionsTests.cs:24` asserts no token means no plugin and no client.
- Not tested: that the HTTP client only issues GET, and that the composed kernel holds only read functions.

## Known exceptions and tensions with the README

- **Token scope.** The control plane asks the user for a GitHub token with the `repo` scope (`src/Scout.ControlPlane/Features/FeatureCatalog.cs:23,24`), which GitHub treats as read and write. The README says a write-scope token violates the rule (README.md:37). The plugin comment says read-only scopes suffice (`src/Scout.Plugin.GitHub/GitHubOptions.cs:8`).
- **Advertised action.** The scope matrix lists "Create issues" under `repo` (`src/Scout.ControlPlane/Features/FeatureCatalog.cs:48`) although the plugin has no such function. An earlier plugin had `create_issue` under `src/old/`, which is outside `Scout.slnx` and not built.
- **Host mutation by the control plane.** `Scout.Host.ControlPlane` can pull and delete Ollama models (`src/Scout.Host.ControlPlane/Services/ModelsGrpcService.cs:22,59`; `src/Scout.ControlPlane/Providers/OllamaApiClient.cs:168`) and start and kill the Ollama process (`src/Scout.ControlPlane/Providers/OllamaProviderService.cs:193`, `src/Scout.ControlPlane/Providers/SystemProcessLauncher.cs:173`). These are driven by the desktop UI over an authenticated local socket (`src/Scout.Host.ControlPlane/ControlPlaneServiceRegistration.cs:30`), never by the model. They mutate the host, so ADR 0009 must rule on whether they fall outside this invariant.
- **Own state.** Scout writes `features.json` and `local-runtimes.json` (`src/Scout.ControlPlane/Features/JsonFileFeatureStateStore.cs:167`, `src/Scout.ControlPlane/Providers/JsonFileLocalRuntimeConfigStore.cs:129`) and the vector database (`src/Scout.Knowledge/KnowledgeServiceCollectionExtensions.cs:107`) under the user's local application data directory. This is Scout's own config and index.

## Related

- Services: [[pages/services/plugin-github]], [[pages/services/llm]], [[pages/services/skills]], [[pages/services/knowledge]], [[pages/services/control-plane]], [[pages/services/host-control-plane]]
- ADR: [[pages/decisions/0009-descriptive-vs-executive-writes]]
- Similar: [[pages/invariants/pluggable-boundaries]]

## Change protocol

To add a write function: draft a superseding ADR, then add the allow-list or marker that makes the rule enforceable, then a test that fails when an unmarked write function reaches the kernel.
