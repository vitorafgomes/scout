---
title: "High-level architecture"
version: "1.1"
date: 2026-04-21
updated: 2026-11-08
changes: "The read-only invariant lines point at the write contract of ADR 0032. Earlier: Migrated from the owner's notes on 2026-10-21"
page_type: longform
status: stub
description: "The orchestration kernel. Hosts Semantic Kernel, loads enabled plugins and the configured LLM provider, and exposes a single HandleAsync(ChatMessage) -> ChatResponse entry point that adapters call."
source:
  - "owner-notes/02-architecture/high-level.md"
reliability: medium
---

# High-level architecture

## Layers

```
+-------------------------------------------------------------+
|                       Chat Platforms                        |
|         Discord   |   Slack   |   Microsoft Teams           |
+-------------------------------------------------------------+
              |           |             |
              v           v             v
+-------------------------------------------------------------+
|                     Chat Adapters                           |
|  Scout.Adapter.Discord | Adapter.Slack | Adapter.Teams      |
|  (normalize inbound events into ChatMessage,                |
|   render outbound responses in platform-native format)      |
+-------------------------------------------------------------+
                           |
                           v
+-------------------------------------------------------------+
|                     Scout.Core                              |
|  - Semantic Kernel host                                     |
|  - Plugin registry                                          |
|  - LLM provider abstraction                                 |
|  - Auth & audit middleware (read-only enforcement)          |
|  - Prompt templates                                         |
+-------------------------------------------------------------+
          |                              |
          v                              v
+--------------------------+   +-----------------------------+
|   LLM Providers          |   |   Feature Plugins           |
|   Llm.Ollama (default)   |   |   Plugin.CloudStatus        |
|   Llm.AzureOpenAI        |   |   Plugin.CodeQA (planned)   |
|   Llm.OpenAI             |   |   Plugin.<future>           |
+--------------------------+   +-----------------------------+
                                          |
                                          v
                               +-----------------------------+
                               |   External Systems          |
                               |   (read-only)               |
                               |   Azure, AWS, Oracle,       |
                               |   GitHub, Azure DevOps      |
                               +-----------------------------+
```

## Components

### Scout.Core

The orchestration kernel. Hosts Semantic Kernel, loads enabled plugins and the configured LLM provider, and exposes a single `HandleAsync(ChatMessage) -> ChatResponse` entry point that adapters call.

Responsibilities:

- Route an incoming message to the right plugin (via SK planner or explicit command parsing).
- Enforce the write contract ([[pages/decisions/0032-per-feature-write-permissions]]): every feature declares the reads and writes it performs; a write runs only under the owner's grant and, when irreversible, after the owner's per-action approval. Updated 2026-11-08: this line first stated an absolute read-only rule enforced at plugin registration, which ADR 0032 superseded.
- Emit telemetry for every external call (source, target, latency, bytes).

### Chat adapters

One process per adapter. Each adapter implements `IChatAdapter` and is chosen via configuration. They translate platform events (message received, slash command, mention) into the internal `ChatMessage` DTO and render `ChatResponse` back to the platform's native message format (Block Kit, Discord embed, Adaptive Card).

Rollout order (see [[pages/decisions/0001-platform-priority|ADR-001]]): Discord → Slack → Teams.

### LLM providers

Implementations of Semantic Kernel's `IChatCompletionService` behind a thin Scout-specific factory. The factory reads `LlmProvider` from config and wires SK accordingly. Contributors can add providers by implementing the factory hook — no change in Core.

### Feature plugins

Semantic Kernel plugins (`KernelPlugin`) with Scout-specific metadata: declared capabilities, required credentials, rate-limit hints.

First-party plugins (both ship in V1):

- **`Plugin.CloudStatus`** — polls Azure Service Health, AWS Health API, Oracle Cloud status endpoints, and Kubernetes clusters (via the official `KubernetesClient` SDK). Returns structured status aggregated across all configured providers. No LLM required for the deterministic path; LLM optional for natural-language rephrasing. See [[pages/domain/cloud-status|spec]].
- **`Plugin.CodeQA`** — indexes one configured GitHub repository, exposes semantic search over code and docs, composes answers via LLM with citations. See [[pages/domain/code-qa|spec]]. Azure DevOps integration is V2.

## Data flow (happy path)

1. User sends `@scout is prod Azure healthy?` in Discord.
2. `Adapter.Discord` receives the Discord.Net event, builds `ChatMessage { user, channel, text }`, calls `Core.HandleAsync`.
3. Core asks the SK planner which plugin handles the intent → `Plugin.CloudStatus`.
4. Plugin calls Azure Service Health API (read-only, authenticated via its own configured credential).
5. Plugin returns a structured `StatusReport`.
6. Core (optionally) passes the report through the LLM to produce a conversational summary.
7. Core returns `ChatResponse` to the adapter.
8. Adapter renders it as a Discord embed and posts back.

## Configuration shape

```json
{
  "ChatAdapter": "Discord",
  "LlmProvider": "Ollama",
  "EnabledPlugins": [ "CloudStatus" ],
  "Adapters": {
    "Discord": { "BotToken": "<from-env>" }
  },
  "Llm": {
    "Ollama": { "Endpoint": "http://localhost:11434", "Model": "llama3.1" }
  },
  "Plugins": {
    "CloudStatus": {
      "Azure": { "SubscriptionId": "<from-env>", "TenantId": "<from-env>" },
      "Aws":   { "Region": "eu-west-1" }
    }
  }
}
```

## Open design questions

- How is the write contract enforced at runtime vs. at startup only? Answered by ADR 0032 (amendment of 2026-11-02): the function gate asks the control plane on every call, and only an allowed call runs.
- Does the planner get called on every message, or do we keep a fast path for slash-command intents?
- How do we scope credentials per plugin without leaking them into LLM prompts?
- Multi-tenant or single-tenant deployment model? (V1 is single-tenant.)

These will become ADRs as they are resolved.
