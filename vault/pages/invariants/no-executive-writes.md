---
title: "Scout performs descriptive writes only"
version: "1.2"
date: 2026-10-21
updated: 2026-10-23
changes: "Optional run-time guard (ADR 0016); Kubernetes plugin: GET-only test against the real client, exact function set; allowed-users refusal classified"
page_type: invariant
status: active
description: "Scout may write content that a human or another agent reads and decides on; it never mutates code, infrastructure, configuration or data on the host. The code upholds this by omission, plus an optional model-judged guard on requests."
enforced_by:
  - "src/Scout.Plugin.GitHub/Abstractions/IGitHubClient.cs:7"
  - "src/Scout.Plugin.GitHub/GitHubClient.cs:157"
  - "src/Scout.Plugin.GitHub/GitHubPlugin.cs:15"
  - "src/Scout.Llm/Guard/ReadOnlyGuardChatCompletionService.cs:87"
  - "src/Scout.Llm/LlmServiceCollectionExtensions.cs:161"
  - "src/Scout.Plugin.Kubernetes/KubernetesPlugin.cs:244"
  - "src/Scout.Plugin.Kubernetes/KubernetesPlugin.cs:223"
verified_by:
  - "tests/Scout.Plugin.GitHub.Tests/GitHubTokenFileHandlerTests.cs:140"
  - "tests/Scout.Plugin.GitHub.Tests/GitHubPluginMetadataTests.cs:25"
  - "tests/Scout.Plugin.GitHub.Tests/GitHubServiceCollectionExtensionsTests.cs:24"
  - "tests/Scout.Llm.Tests/Guard/ReadOnlyGuardChatCompletionServiceTests.cs:79"
  - "tests/Scout.Llm.Tests/Guard/ReadOnlyGuardCompositionTests.cs:111"
  - "tests/Scout.Plugin.Kubernetes.Tests/KubernetesPluginTests.cs:250"
  - "tests/Scout.Plugin.Kubernetes.Tests/KubernetesPluginTests.cs:239"
  - "tests/Scout.Plugin.Kubernetes.Tests/KubernetesPluginTests.cs:320"
source:
  - "src/Scout.Plugin.GitHub/"
  - "src/Scout.Plugin.Kubernetes/"
  - "src/Scout.Llm/"
  - "src/Scout.Skills/"
  - "src/Scout.Knowledge/"
  - "src/Scout.ControlPlane/"
  - "tests/Scout.Plugin.GitHub.Tests/"
  - "tests/Scout.Plugin.Kubernetes.Tests/"
reliability: high
---

# Scout performs descriptive writes only

> Every write Scout makes is content a person or another agent reads and decides on (a chat reply, an issue body); no code path may mutate code, infrastructure, configuration or data on the host or in a connected system.

## The gap

Nothing in code stops a plugin from exposing a write function. The kernel takes every `KernelPlugin` registered in DI (`src/Scout.Llm/LlmServiceCollectionExtensions.cs:125`) and offers all of their functions to the model with `FunctionChoiceBehavior.Auto()` (`src/Scout.Llm/ChatGateway.cs:345`, `src/Scout.Knowledge/ProjectExpertAgentFactory.cs:84`). There is no allow-list, no read-only marker on a function, and no test that fails when a write function appears. Read-only holds today because the write methods do not exist. The optional request guard below judges what the user asks for, not what a function can do, so it does not close this gap.

## Why this exists

Scout answers questions in shared chat channels. A model that can call a mutating tool turns a prompt in a public channel into a change on a real system. The README states the rule as "never writes"; [[pages/decisions/0009-descriptive-vs-executive-writes]] refines it to descriptive versus executive writes.

## Scope

- **Applies to:** every `KernelPlugin` the host registers, the GitHub client, the Kubernetes clients, the chat adapters, and the control plane.
- **Does not cover:** Scout's own state (below), and replies posted to the chat platform, which are descriptive writes by definition. That includes the allowed-users refusal ("Scout only answers its configured users." or the setup text), which goes through the same sink to the originating channel only and is the one thing a refused message causes ([[pages/decisions/0021-allowed-users]], `src/Scout.Conversation/ConversationConductor.cs:60`). The allow-list decides who gets replies; it never authorizes a write.

## Where it is held

- `src/Scout.Plugin.GitHub/Abstractions/IGitHubClient.cs:7` declares four read methods and no others; the interface comment says no create, merge, push or comment operation exists by design.
- `src/Scout.Plugin.GitHub/GitHubClient.cs:140` and `:157` are the only HTTP calls, both `GetAsync`.
- `src/Scout.Plugin.GitHub/GitHubPlugin.cs:29,51,76,101` expose `list_repositories`, `list_pull_requests`, `list_issues`, `get_repository`.
- `src/Scout.Skills/SkillsPlugin.cs:27,40` (`list_skills`, `load_skill`) and `src/Scout.Knowledge/ProjectKnowledgePlugin.cs:26,41,53` (`search_project`, `find_symbol`, `list_projects`) only read.
- `src/Scout.Plugin.GitHub/GitHubServiceCollectionExtensions.cs:44` registers nothing when neither a token nor a token file is set.
- **Request guard, when a TypeSafe key is set** ([[pages/decisions/0016-jev-read-only-guard]]). `src/Scout.Llm/LlmServiceCollectionExtensions.cs:161` replaces the kernel's chat service with `ReadOnlyGuardChatCompletionService`; `src/Scout.Llm/Guard/ReadOnlyGuardChatCompletionService.cs:87` asks TypeSafe's Jev whether the latest user message, read with up to 12 messages before it, asks for a change and, at or above the threshold, returns a refusal without calling the model (`:74` streaming, `:59` non-streaming). It fails open: a TypeSafe error logs a Warning and the request reaches the model (`:116`). With no key it is not registered.
- Kubernetes is strict read-only ([[pages/decisions/0009-descriptive-vs-executive-writes]], [[pages/decisions/0020-kubernetes-from-local-kubeconfig]]). `src/Scout.Plugin.Kubernetes/KubernetesPlugin.cs:68` to `:176` expose `list_contexts`, `list_namespaces`, `list_pods`, `describe_pod`, `list_deployments` and `list_events`, each a list or read call; no log, secret, config map, exec, attach, port-forward, proxy or watch call exists, and a context the owner did not allow is refused before a client is built (`src/Scout.Plugin.Kubernetes/KubernetesPlugin.cs:244`). Because the library concatenates the namespace and pod name into the request path, a value that is not a DNS-1123 Kubernetes name is refused before any request (`src/Scout.Plugin.Kubernetes/KubernetesPlugin.cs:223`), so model input cannot turn a GET on a pod into a GET on secrets or a service proxy. The owner's credentials may allow writes, so read-only rests on the plugin unless the owner uses the read-only `view` user the card recommends.
- Through the desktop sign-in the GitHub token belongs to a GitHub App with read-only permissions, so GitHub itself refuses a write whatever the code does ([[pages/decisions/0019-github-app-device-flow]]) This holds for the browser method only; a token the owner pastes is capped by its own grant, and read-only then rests on the GET-only client alone.

## How it is verified

- `tests/Scout.Plugin.GitHub.Tests/GitHubPluginMetadataTests.cs:25` asserts the four expected functions exist. It does not assert that no other function exists, so adding `create_issue` would not fail it.
- `tests/Scout.Plugin.GitHub.Tests/GitHubServiceCollectionExtensionsTests.cs:24` asserts no token means no plugin and no client.
- `tests/Scout.Llm.Tests/Guard/ReadOnlyGuardChatCompletionServiceTests.cs:79` asserts that a request Jev scores at 0.9 is refused with the fixed text and the model is never called; `:108` that a request scored 0.1 reaches the model unchanged; `:126` that a TypeSafe 500 still lets the model answer.
- `tests/Scout.Llm.Tests/Guard/ReadOnlyGuardCompositionTests.cs:100` and `:111` assert that the composed gateway refuses on the direct chat path and on the Project Expert agent path; `tests/Scout.Llm.Tests/LlmServiceCollectionExtensionsTests.cs:260` that every provider's kernel chat service is the guard when a key is set.
- `tests/Scout.Plugin.GitHub.Tests/GitHubTokenFileHandlerTests.cs:140` invokes every GitHub function through the real composition and fails on any request to `api.github.com` that is not a GET; `ui/packages/main/test/oauth-github.test.ts:455` does the same for the desktop's own calls.
- `tests/Scout.Plugin.Kubernetes.Tests/KubernetesPluginTests.cs:320` and `:337` send `../secrets/x`, `x/services/y:80/proxy/z?`, `default%2Fsecrets` and a field-selector injection as a pod name and as a namespace, and fail on any recorded request.
- `tests/Scout.Plugin.Kubernetes.Tests/KubernetesPluginTests.cs:250` invokes every Kubernetes function through a kernel, with the real KubernetesClient against an in-process API server that records each request before routing it, and fails on any request that is not a GET; `:239` asserts the exact set of six functions, so a seventh, such as `delete_pod`, fails it.
- Not tested: that the composed kernel holds only read functions. The guard does not look at tool calls (see [[pages/roadmap/followups]]).

## Known exceptions and tensions with the README

- **Token scope (reopened 2026-10-23 for pasted tokens).** The catalog asked for a `repo`-scope token and listed "Create issues"; it now lists only the GitHub App's read permissions (`src/Scout.ControlPlane/Features/FeatureCatalog.cs:42`, `tests/Scout.ControlPlane.Tests/Features/FeatureCatalogTests.cs:27`). A host configured with `Scout:Plugins:GitHub:Token` still gets whatever scopes that token carries. An earlier plugin had `create_issue` under `src/old/`, which is outside `Scout.slnx` and not built. The desktop "Use a token" method accepts any token the owner pastes, including a classic token with `repo` scope, and hands it to the worker as `SCOUT__PLUGINS__GITHUB__TOKEN`; the card warns when the scope header lists a write scope, and read-only for that method rests on the GET-only client ([[pages/decisions/0019-github-app-device-flow]], `ui/packages/main/test/oauth-github-token.test.ts:133`).
- **Host mutation by the control plane.** `Scout.Host.ControlPlane` can pull and delete Ollama models (`src/Scout.Host.ControlPlane/Services/ModelsGrpcService.cs:22,59`; `src/Scout.ControlPlane/Providers/OllamaApiClient.cs:168`) and start and kill the Ollama process (`src/Scout.ControlPlane/Providers/OllamaProviderService.cs:193`, `src/Scout.ControlPlane/Providers/SystemProcessLauncher.cs:173`). These are driven by the desktop UI over an authenticated local socket (`src/Scout.Host.ControlPlane/ControlPlaneServiceRegistration.cs:30`), never by the model. They mutate the host, so ADR 0009 must rule on whether they fall outside this invariant.
- **Own state.** Scout writes `features.json` and `local-runtimes.json` (`src/Scout.ControlPlane/Features/JsonFileFeatureStateStore.cs:167`, `src/Scout.ControlPlane/Providers/JsonFileLocalRuntimeConfigStore.cs:129`) and the vector database (`src/Scout.Knowledge/KnowledgeServiceCollectionExtensions.cs:107`) under the user's local application data directory. This is Scout's own config and index.

## Related

- Services: [[pages/services/plugin-github]], [[pages/services/plugin-kubernetes]], [[pages/services/llm]], [[pages/services/skills]], [[pages/services/knowledge]], [[pages/services/control-plane]], [[pages/services/host-control-plane]]
- ADR: [[pages/decisions/0009-descriptive-vs-executive-writes]], [[pages/decisions/0020-kubernetes-from-local-kubeconfig]]
- Similar: [[pages/invariants/pluggable-boundaries]]

## Change protocol

To add a write function: draft a superseding ADR, then add the allow-list or marker that makes the rule enforceable, then a test that fails when an unmarked write function reaches the kernel.
