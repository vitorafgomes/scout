---
title: "All LLM communication routes through Semantic Kernel (Scout.Llm gateway)"
version: "1.0"
date: 2026-05-28
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: adr
status: accepted
description: "Decision that all LLM communication in Scout routes through Semantic Kernel."
source:
  - "obsidian-vault/scout/06-decisions/2026-05-28-llm-communication-through-semantic-kernel.md"
reliability: medium
---

# All LLM communication routes through Semantic Kernel (Scout.Llm gateway)

## Context

Scout's stated goal is that all model communication flows through Microsoft
Semantic Kernel (see project-scout-semantic-kernel-hub (private note) context). The
pre-restructure code only partially honoured this:

- Chat completion went through SK correctly: `LlmFallbackRouter`, the agent
  router, and the CodeQA `LlmComposer` all consumed
  `IChatCompletionService`, and `OllamaLlmProviderFactory` registered it via
  `builder.AddOllamaChatCompletion(...)`.
- **Embeddings bypassed SK entirely.** `Scout.Llm.Ollama.OllamaEmbedder`
  constructed an `OllamaSharp.OllamaApiClient` directly and consumed it via
  `Microsoft.Extensions.AI.IEmbeddingGenerator`. Its own XML comment cited the
  experimental `SKEXP0070` status of the SK Ollama connector as the reason to
  avoid the kernel. Embedding generation is model communication, so this
  violated the rule.
- **Configuration had two sources of truth** for "where is Ollama": chat read
  `Scout:Llm` (`Endpoint` + `Model`), while embeddings read
  `CodeQAOptions.Embeddings.BaseUrl` + `Model`. Two endpoints, drift-prone.
- The chat completion service was built ad hoc: `LlmChatServiceCollectionExtensions`
  built a throwaway kernel at registration time purely to extract
  `IChatCompletionService` (see the negative consequence noted in
  [[pages/decisions/0010-scout-host-kernel-accessor]]).

The restructure archives the previous source tree under `src/old/` and rebuilds
fresh. The first new project is the single gateway for all chat + LLM
communication.

## Decision

Introduce `src/Scout.Llm` as the sole gateway for LLM communication. Every model
call — chat completion and embeddings — is registered on and resolved from a
Semantic Kernel. No gateway code constructs a provider SDK client directly for
inference.

Concretely:

- `Scout.Llm.Abstractions.ILlmProviderFactory` exposes
  `void AddToKernel(IKernelBuilder builder)` which registers **both** chat
  completion and embedding generation. A provider configures its full
  capability surface atomically; consumers always get a fully-wired kernel.
- `Scout.Llm.Providers.Ollama.OllamaLlmProviderFactory.AddToKernel`:
  - chat: `builder.AddOllamaChatCompletion(chatModel, endpoint)` (stable surface).
  - embeddings: `builder.AddOllamaEmbeddingGenerator(embeddingModel, endpoint)`,
    which registers the stable
    `Microsoft.Extensions.AI.IEmbeddingGenerator<string, Embedding<float>>` into
    the kernel's service provider.
- **Single configuration source** `Scout:Llm`: one `Ollama.Endpoint` shared by
  chat and embeddings; models split per role (`Chat.Model`,
  `Embeddings.Model`). This is correct single-sourcing — a chat model and an
  embed model legitimately differ, but they share one endpoint/provider.
- `AddScoutLlm` builds one shared `Kernel` singleton from the active provider
  factory and exposes `IChatCompletionService` and `IEmbeddingGenerator` resolved
  from it, replacing the old throwaway-kernel hack.

### SKEXP0070 acceptance

`AddOllamaEmbeddingGenerator` (and the alpha Ollama connector generally) carries
the experimental `SKEXP0070` attribute. We accept it under a **scoped**
`#pragma warning disable SKEXP0070` / `restore` around the registration calls
only (two private helpers in `OllamaLlmProviderFactory`). The consumed type
(`IEmbeddingGenerator<string, Embedding<float>>`) is stable, so no experimental
surface leaks to consumers, and `LlmServiceCollectionExtensions` needs no
suppression at all. This is strictly better than the previous state, which
suppressed `SKEXP0070` project-wide *and* still bypassed the kernel.

## Alternatives considered

### Register an externally-built `IEmbeddingGenerator` into the kernel's DI

Build an `OllamaApiClient`/`IEmbeddingGenerator` in user code and add it via
`builder.Services.AddSingleton`. Rejected: this still `new`s the provider SDK
client — the exact pattern being eliminated. It would be "through the kernel's
DI container" but not "through SK's connector": hollow compliance.

### Keep the old bespoke `IEmbedder` facade

The old `IEmbedder` (batching, dimension validation, input-order guarantees) was
a CodeQA-plugin concern. Rejected for the gateway: it re-wraps and hides the SK
type. The gateway exposes the SK-native `IEmbeddingGenerator`; CodeQA, when
rebuilt, can wrap it with its own batching/validation policy as a consumer.

### Separate `AddChatToKernel` + `AddEmbeddingsToKernel`

Rejected: invites partially-configured kernels and pushes capability composition
up into the host. A provider that genuinely lacks embeddings should surface as a
missing kernel service (caught by a startup smoke check), not as an abstraction
split.

## Consequences

### Positive

- **Rule is structurally enforced.** Embeddings can no longer bypass SK because
  the only embedding entry point is the kernel registration. A registration test
  asserts `IChatCompletionService` and `IEmbeddingGenerator` both resolve from
  the built kernel — the regression guard for the original violation.
- **One source of truth** for the Ollama endpoint, at config and at runtime
  (one shared `Kernel`).
- **Provider isolation.** Only `Providers/Ollama/` touches the alpha connector;
  a future `Scout.Llm.AzureOpenAI` is an additive provider with no churn to the
  abstraction layer or consumers — preserves the "pluggable, no vendor lock-in"
  invariant.

### Negative

- **Experimental dependency in the build.** `SKEXP0070` is accepted. Mitigation:
  scoped suppression, stable consumed type, this ADR, and the alpha connector
  version is pinned in `Directory.Packages.props`
  (`Microsoft.SemanticKernel.Connectors.Ollama 1.74.0-alpha`). If the connector
  API drifts, the fallback is `AddOllamaTextEmbeddingGeneration`
  (`ITextEmbeddingGenerationService`, also `SKEXP0070`, still kernel-routed).
- **Gateway carries connector packages.** Expected: `Scout.Llm` is the LLM
  project, so the old "keep Scout.Core free of the alpha package" rationale for a
  separate `Scout.Llm.Ollama` project no longer applies. The split returns only
  when a second provider lands.

### Operational

- The HTTP boundary (Ollama inference) has a mandatory failure-injection test:
  the factory accepts an injectable `HttpClient` (test seam) driven by a stub
  `HttpMessageHandler` returning 500 / timeout / dropped connection; the call
  surfaces a failure rather than hanging or returning silent empty results.
- PR quality gates apply (Scout CLAUDE.md): `architect-reviewer` (defines the new
  LLM provider abstraction), `security-auditor` (external API boundary).
- Daemon lifecycle/model-management (`/api/tags`, `/api/pull`, `/api/delete`) is
  NOT inference and stays out of this gateway; it remains direct HTTP in the
  worker's provider-management layer.

## References

- First project of the restructure: `src/Scout.Llm` (branch
  `feat/scout-llm-gateway`); old tree frozen under `src/old/`.
- Supersedes the throwaway-kernel registration hack noted in
  [[pages/decisions/0010-scout-host-kernel-accessor]].
- Related invariant: [[pages/decisions/0009-descriptive-vs-executive-writes]].
- Streaming-first mandate (feedback-scout-streaming-first (private note)) shapes the chat
  consumers built on top of this gateway.
- SK guidance: Microsoft Learn — Semantic Kernel quick start, plugins, and agent
  framework (C#).
