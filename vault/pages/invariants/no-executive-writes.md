---
title: "Scout performs descriptive writes only"
version: "1.1"
date: 2026-10-21
updated: 2026-10-21
changes: "Re-verified against the imported development line on 2026-10-21"
page_type: invariant
status: active
description: "Scout may write content that a human or another agent reads and decides on; it never mutates code, infrastructure, configuration or data on the host. The code upholds this by omission, not by a gate."
enforced_by:
  - "src/Scout.Plugin.GitHub/Abstractions/IGitHubClient.cs:7"
  - "src/Scout.Plugin.GitHub/GitHubClient.cs:157"
  - "src/Scout.Plugin.Common/Abstractions/IScoutLocalStore.cs:9"
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
  - "src/Scout.Host.Console/"
  - "src/Scout.Plugin.Common/"
  - "tests/Scout.Plugin.GitHub.Tests/"
reliability: high
---

# Scout performs descriptive writes only

> Every write Scout makes is content a person or another agent reads and decides on (a chat reply, an issue body); no code path may mutate code, infrastructure, configuration or data on the host or in a connected system.

## The gap

Nothing in code stops a plugin from exposing a write function. The kernel takes every `KernelPlugin` registered in DI (`src/Scout.Llm/LlmServiceCollectionExtensions.cs:96`) and offers all of their functions to the model with `FunctionChoiceBehavior.Auto()` (`src/Scout.Llm/ChatGateway.cs:311`, `src/Scout.Knowledge/ProjectExpertAgentFactory.cs:84`). There is still no allow-list, no function-invocation filter, no read-only marker on a function, and no test that fails when a write function appears. The per-request identity added since (`src/Scout.Conversation/ConversationConductor.cs:65`) is documented as gating output disclosure only, never a write (`:63`). External read-only holds today because the external write methods do not exist.

## Why this exists

Scout answers questions in shared chat channels. A model that can call a mutating tool turns a prompt in a public channel into a change on a real system. The README states the rule as "never writes to any external system" (`README.md:6`, `:163`); [[pages/decisions/0009-descriptive-vs-executive-writes]] refines it to descriptive versus executive writes.

## Scope

- **Applies to:** every `KernelPlugin` the host registers (23 plugin projects, wired at `src/Scout.Host.Console/HostComposition.cs:80` to `:121`), the chat adapters, and the control plane.
- **Does not cover:** Scout's own state (below), and replies posted to the chat platform, which are descriptive writes by definition.

## Where it is held

- `src/Scout.Plugin.GitHub/Abstractions/IGitHubClient.cs:7` declares four read methods and no others; `src/Scout.Plugin.GitHub/GitHubClient.cs:140` and `:157` are its only HTTP calls, both `GetAsync`. `GitHubPlugin.cs:29,51,76,101` expose the four matching functions.
- `src/Scout.Skills/SkillsPlugin.cs:27,40` and `src/Scout.Knowledge/ProjectKnowledgePlugin.cs:26,41,53` only read.
- `src/Scout.Plugin.GitHub/GitHubServiceCollectionExtensions.cs:38` registers nothing when no token is set.
- No other plugin issues a write verb to an external system. The one non-GET call is `ParcelsClient` posting a search to a tracking API (`src/Scout.Plugin.Parcels/ParcelsClient.cs:28`), commented as a read-only search that registers no tracker (`:21`).
- The adapters write only their own reply: Discord `SendMessageAsync` and `ModifyAsync`, Slack `Chat.PostMessage` and `Chat.Update`, Teams `SendActivityAsync`; no delete, reaction or file call exists in `src/Scout.Adapter.*`.

## How it is verified

- `tests/Scout.Plugin.GitHub.Tests/GitHubPluginMetadataTests.cs:25` asserts the four expected functions exist. It does not assert that no other function exists, so adding `create_issue` would not fail it.
- `tests/Scout.Plugin.GitHub.Tests/GitHubServiceCollectionExtensionsTests.cs:24` asserts no token means no plugin and no client.
- Not tested: that any HTTP client only issues GET, and that the composed kernel holds only read functions or only Scout-local writes.

## Known exceptions and tensions with the README

- **Model-callable writes to Scout's own store.** Several plugins let the model change local data persisted by `JsonFileScoutLocalStore` under local application data, `Scout/plugin-data` (`src/Scout.Plugin.Common/JsonFileScoutLocalStore.cs:33`, write at `:96,103`): `add_note` and `delete_note` (`src/Scout.Plugin.Notes/NotesPlugin.cs:39,145`), `add_reminder`, `complete_reminder`, `delete_reminder` (`src/Scout.Plugin.Reminders/RemindersPlugin.cs:63,168,227`), `add_to_list`, `remove_from_list`, `clear_list` (`src/Scout.Plugin.Lists/ListsPlugin.cs:37,119,189`), `record_expense`, `record_income` (`src/Scout.Plugin.Ledger/LedgerPlugin.cs:78,138`), `watch_package`, `unwatch_package` (`src/Scout.Plugin.Parcels/ParcelsPlugin.cs:83,158`) and `save_to_reading_list` (`src/Scout.Plugin.News/NewsPlugin.cs:151`). The host comments call these "Scout-local descriptive-write only" (`src/Scout.Host.Console/HostComposition.cs:98`). They are not external writes, but they contradict a literal reading of "read-only" and include deletes. ADR 0009 should rule on them.
- **Token scope.** The control plane asks the user for a GitHub token with the `repo` and `read:org` scopes (`src/Scout.ControlPlane/Features/FeatureCatalog.cs:26`); `repo` is read and write on GitHub. The README and plugin comment say read-only scopes suffice (`README.md:128`, `src/Scout.Plugin.GitHub/GitHubOptions.cs:11`).
- **Advertised action.** The scope matrix lists "Create issues" under `repo` (`src/Scout.ControlPlane/Features/FeatureCatalog.cs:51`) although the plugin has no such function. The catalog marks the feature `WriteClass.None` (`:41`).
- **Host mutation by the control plane.** Still present. `Scout.Host.ControlPlane` can pull and delete Ollama models (`src/Scout.Host.ControlPlane/Services/ModelsGrpcService.cs:22,59`; `src/Scout.ControlPlane/Providers/OllamaApiClient.cs:124,168`) and start and kill the Ollama process (`src/Scout.ControlPlane/Providers/OllamaProviderService.cs:193`, `src/Scout.ControlPlane/Providers/SystemProcessLauncher.cs:173`), behind a session token (`src/Scout.Host.ControlPlane/ControlPlaneServiceRegistration.cs:30`). New: the chat host also serves start, stop and restart (not pull or delete) over loopback HTTP, fail-closed without a token (`src/Scout.Host.Console/ControlPlaneHttpHost.cs:59,281,285`), so the chat process can now spawn and kill `ollama serve`. Both are driven by the desktop UI, never by the model.
- **Own state.** Scout writes `features.json` and `local-runtimes.json` (`src/Scout.ControlPlane/Features/JsonFileFeatureStateStore.cs:167`, `src/Scout.ControlPlane/Providers/JsonFileLocalRuntimeConfigStore.cs:129`) and the vector database (`src/Scout.Knowledge/KnowledgeServiceCollectionExtensions.cs:107`) under the user's local application data directory. This is Scout's own config and index.

## Related

- Services: [[pages/services/plugin-github]], [[pages/services/llm]], [[pages/services/skills]], [[pages/services/knowledge]], [[pages/services/control-plane]], [[pages/services/host-control-plane]], [[pages/services/host-console]]
- ADR: [[pages/decisions/0009-descriptive-vs-executive-writes]]
- Similar: [[pages/invariants/pluggable-boundaries]]

## Change protocol

To add a write function: draft a superseding ADR, then add the allow-list or marker that makes the rule enforceable, then a test that fails when an unmarked write function reaches the kernel.
