---
title: "Adapters, LLM providers and plugins are swapped by configuration"
version: "1.1"
date: 2026-10-21
updated: 2026-10-23
changes: "Citations moved by the read-only guard change; Log label gap narrowed earlier"
page_type: invariant
status: active
description: "Chat adapters, LLM providers and plugins sit behind abstractions and are selected or gated by Scout: configuration; the core names no vendor. The set of options is closed at compile time."
enforced_by:
  - "src/Scout.Conversation/Abstractions/IChatAdapter.cs:11"
  - "src/Scout.Llm/Abstractions/ILlmProviderFactory.cs:17"
  - "src/Scout.Llm/LlmServiceCollectionExtensions.cs:71"
  - "src/Scout.Llm/LlmServiceCollectionExtensions.cs:123"
verified_by:
  - "tests/Scout.Llm.Tests/LlmServiceCollectionExtensionsTests.cs:224"
  - "tests/Scout.Llm.Tests/LlmServiceCollectionExtensionsTests.cs:299"
  - "tests/Scout.Host.Console.Tests/HostCompositionSmokeTests.cs:181"
source:
  - "src/Scout.Conversation/"
  - "src/Scout.Llm/"
  - "src/Scout.Host.Console/"
  - "tests/Scout.Llm.Tests/"
  - "tests/Scout.Host.Console.Tests/"
reliability: high
---

# Adapters, LLM providers and plugins are swapped by configuration

> The conversation core and the host talk to `IChatAdapter`, `IChatGateway` and `KernelPlugin`, never to Discord, Ollama or GitHub types; which implementation runs is decided by `Scout:` configuration.

## Why this exists

Scout is open source and ships to users who have different chat platforms and different LLM budgets. A vendor type in the core would force every user to carry it.

## Scope

- **Applies to:** `Scout.Conversation`, `Scout.Host.Console`, and every consumer of `IChatGateway`.
- **Does not cover:** adding a new vendor. That needs a code change and a rebuild (see Known gaps).

## Where it is enforced

- **Adapters.** `src/Scout.Conversation/Abstractions/IChatAdapter.cs:11` and `src/Scout.Conversation/Abstractions/IResponseSink.cs:17` are the only surface the core sees. Each adapter registers itself as `IChatAdapter` only when its credentials are present: `src/Scout.Adapter.Discord/DiscordServiceCollectionExtensions.cs:30`, `src/Scout.Adapter.Slack/SlackServiceCollectionExtensions.cs:32`, `src/Scout.Adapter.Teams/TeamsServiceCollectionExtensions.cs:37`. The host starts whatever set is registered: `src/Scout.Host.Console/ConversationWorker.cs:40`.
- **LLM providers.** `src/Scout.Llm/Abstractions/ILlmProviderFactory.cs:17` is the contract. `src/Scout.Llm/LlmServiceCollectionExtensions.cs:71` switches on `Scout:Llm:Provider` over ten factories (nine providers, ChatGptPlan among them, and `None`, which answers with a fixed message and calls no model) and fails startup on an unknown name (`:103`). Consumers receive `IChatGateway` (`src/Scout.Llm/Abstractions/IChatGateway.cs:8`), built at `src/Scout.Llm/LlmServiceCollectionExtensions.cs:147`, and a standalone embedding generator (`src/Scout.Llm/LlmServiceCollectionExtensions.cs:144`).
- **Plugins.** The kernel adds every `KernelPlugin` found in DI (`src/Scout.Llm/LlmServiceCollectionExtensions.cs:123`). GitHub, Kubernetes, Skills and ProjectKnowledge each register one only when enabled: `src/Scout.Plugin.GitHub/GitHubServiceCollectionExtensions.cs:44`, `src/Scout.Plugin.Kubernetes/KubernetesServiceCollectionExtensions.cs:33`, `src/Scout.Skills/SkillsServiceCollectionExtensions.cs:43`, `src/Scout.Knowledge/KnowledgeServiceCollectionExtensions.cs:74`.

## How it is verified

- `tests/Scout.Llm.Tests/LlmServiceCollectionExtensionsTests.cs:39,107,204` resolve the expected factory for Ollama, Gemini and six more providers; `:281` asserts an unknown name throws and lists the supported ones.
- `tests/Scout.Llm.Tests/LlmServiceCollectionExtensionsTests.cs:299` asserts a registered plugin lands on the kernel.
- `tests/Scout.Adapter.Discord.Tests/DiscordServiceCollectionExtensionsTests.cs:39`, `tests/Scout.Adapter.Slack.Tests/SlackServiceCollectionExtensionsTests.cs:52` and `tests/Scout.Adapter.Teams.Tests/TeamsServiceCollectionExtensionsTests.cs:39` assert no credentials means no adapter.
- `tests/Scout.Host.Console.Tests/HostCompositionSmokeTests.cs:181` builds the host with no adapter and no project and asserts it still resolves.
- `tests/Scout.Knowledge.Tests/KnowledgeServiceCollectionExtensionsTests.cs:109` asserts the plugin is absent with no project configured.

## Known gaps

- **The provider list is closed.** The switch at `src/Scout.Llm/LlmServiceCollectionExtensions.cs:71` has no registry or discovery; a new provider means editing it.
- **Vendor packages are linked in.** `src/Scout.Llm/Scout.Llm.csproj` references the connectors for all providers, and `Scout.Conversation` and the adapters inherit them through the project reference. `Scout.Host.Console` references all three adapters and the GitHub and Kubernetes plugins at compile time; the `KubernetesClient` package stays inside `Scout.Plugin.Kubernetes`, which exposes only a `KernelPlugin` (`src/Scout.Host.Console/Scout.Host.Console.csproj:15`), so swapping is by configuration, not by deployment.
- **OpenAI types leak into the core.** `src/Scout.Llm/ChatGateway.cs:341` builds `OpenAIPromptExecutionSettings` for every provider, and so does `src/Scout.Knowledge/ProjectExpertAgentFactory.cs:82`.
- **Log label covers three providers.** `src/Scout.Llm/ChatGateway.cs:60` describes Ollama, OpenAI and Gemini; any other provider logs its name with `unknown` endpoint and model.
- **Teams has no live inbound path.** `TeamsChatAdapter.ProcessActivityAsync` (`src/Scout.Adapter.Teams/TeamsChatAdapter.cs:107`) has no production caller; the webhook is deliberately not wired (`src/Scout.Adapter.Teams/TeamsServiceCollectionExtensions.cs:18`).

## Related

- Services: [[pages/services/llm]], [[pages/services/conversation]], [[pages/services/host-console]], [[pages/services/adapter-discord]], [[pages/services/adapter-slack]], [[pages/services/adapter-teams]], [[pages/services/plugin-github]]
- Similar: [[pages/invariants/local-first-default]], [[pages/invariants/no-executive-writes]]

## Change protocol

To close a gap (for example a provider registry), draft a superseding ADR first, then change the switch and keep the unknown-provider test.
