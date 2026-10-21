---
title: "Llm"
version: "1.1"
date: 2026-10-21
updated: 2026-10-21
changes: "Re-verified against the imported development line on 2026-10-21"
page_type: service
status: active
description: "Selects one LLM provider by configuration, builds the shared Semantic Kernel, and exposes a streaming chat gateway and an embedding generator to the rest of Scout."
source: ["src/Scout.Llm/", "tests/Scout.Llm.Tests/"]
reliability: high
---

# Llm

> The single door to a language model: one provider, one shared kernel, one streaming gateway.

## To change here, read

- [[pages/invariants/pluggable-boundaries]]
- [[pages/invariants/local-first-default]]
- [[pages/invariants/secrets-never-in-source]]
- [[pages/invariants/no-executive-writes]]
- [[pages/conventions/testing]]

## Responsibility

Owns provider selection, the shared `Kernel` (chat completion, embeddings, every registered plugin) and automatic function calling. Does not own prompts or history ([[pages/services/conversation]]) nor the plugins themselves ([[pages/services/plugin-github]], [[pages/services/skills]], [[pages/services/knowledge]]).

## Public contract

- **Entry point:** `AddScoutLlm(IServiceCollection, IConfiguration)` at `src/Scout.Llm/LlmServiceCollectionExtensions.cs:39`.
- **Consumer abstraction:** `IChatGateway` with `StreamAsync` and `CompleteAsync` (`src/Scout.Llm/Abstractions/IChatGateway.cs:8`); request record `ChatRequest` (`src/Scout.Llm/Abstractions/ChatRequest.cs:9`); history turn `ConversationTurn` (`src/Scout.Llm/Abstractions/ConversationTurn.cs:22`); rate-limit signal `LlmRateLimitException` (`src/Scout.Llm/Abstractions/LlmRateLimitException.cs:9`) and provider-down signal `LlmProviderUnavailableException` (`src/Scout.Llm/Abstractions/LlmProviderUnavailableException.cs:10`); both are turned into a friendly final message by [[pages/services/conversation]].
- **Provider seam:** `ILlmProviderFactory` (`src/Scout.Llm/Abstractions/ILlmProviderFactory.cs:17`) with `AddToKernel` and `CreateEmbeddingGenerator`. Eight factories live under `src/Scout.Llm/Providers/`: Ollama (`Ollama/OllamaLlmProviderFactory.cs:103` name), OpenAI, OpenRouter, AzureOpenAI, Mistral, HuggingFace, AzureAIInference, Gemini; seven of them are hosted or cloud APIs; each has an `Add<Name>LlmProvider` extension.
- **Configuration keys:** `Scout:Llm:Provider`; one block per provider under `Scout:Llm:<Provider>` with `ApiKey`, `Endpoint`, `Chat:Model`, `Embeddings:Model` (AzureOpenAI uses `Chat:Deployment` and `Embeddings:Deployment`); Ollama also reads `Embeddings:MaxBatchSize`, `Embeddings:ExpectedDimensions`, `Embeddings:HttpTimeoutSeconds`. Options types: `src/Scout.Llm/Configuration/LlmOptions.cs:12`.
- **External boundaries:** the chosen provider's HTTP API (Ollama on localhost by default, `src/Scout.Llm/Configuration/LlmOptions.cs:207`; hosted APIs otherwise).

## How it works

```mermaid
flowchart LR
    cfg["Scout:Llm:Provider"] --> sw["switch (LlmServiceCollectionExtensions.cs:54)"]
    sw --> fac["ILlmProviderFactory"]
    fac --> k["Kernel + KernelPlugin list (:87)"]
    k --> gw["ChatGateway (ChatGateway.cs:28)"]
    fac --> emb["IEmbeddingGenerator (:117)"]
    gw --> out["IAsyncEnumerable of text chunks"]
```

The kernel adds every `KernelPlugin` from DI (`src/Scout.Llm/LlmServiceCollectionExtensions.cs:96`). The gateway streams through the Project Expert agent when one is registered (`src/Scout.Llm/ChatGateway.cs:73`) and through plain chat otherwise (`:82`). HTTP 429 becomes `LlmRateLimitException` (`:150`, `:233`); a transport failure (refused connection, socket or DNS error, internal timeout) becomes `LlmProviderUnavailableException` through `IsProviderDown` (`:154`, `:237`, `:258`). The embedding generator is built from the factory, not from the kernel, to avoid a construction cycle (`src/Scout.Llm/LlmServiceCollectionExtensions.cs:117`).

## Invariants

- [[pages/invariants/pluggable-boundaries]]: the provider switch is the swap point; the list is closed.
- [[pages/invariants/local-first-default]]: blank provider resolves to Ollama (`src/Scout.Llm/LlmServiceCollectionExtensions.cs:48`; the option default is `"Ollama"` at `src/Scout.Llm/Configuration/LlmOptions.cs:16` and the committed host config sets it again at `src/Scout.Host.Console/appsettings.json:10`).
- [[pages/invariants/secrets-never-in-source]]: every provider `ApiKey` defaults to empty or null (`src/Scout.Llm/Configuration/LlmOptions.cs:51,77,87`).
- [[pages/invariants/no-executive-writes]]: the gateway offers all kernel functions with `FunctionChoiceBehavior.Auto()` (`src/Scout.Llm/ChatGateway.cs:311`) and has no allow-list; no function-invocation filter exists in `src/`. The Project Expert agent path does the same (`src/Scout.Knowledge/ProjectExpertAgentFactory.cs:84`).

## Hot spots

- `src/Scout.Llm/ChatGateway.cs:307` builds `OpenAIPromptExecutionSettings` for every provider.
- `src/Scout.Llm/ChatGateway.cs:50` logs the Ollama endpoint and model regardless of provider.
- `src/Scout.Llm/LlmServiceCollectionExtensions.cs:54` is the closed provider switch.

## Testing footprint

`tests/Scout.Llm.Tests` (166 test attributes, 25 files): composition and unknown-provider behaviour (`LlmServiceCollectionExtensionsTests.cs`), gateway streaming, agent path, function calling, rate limit and provider-unavailable (`ChatGateway*Tests.cs`), a regression for the kernel and embedder construction cycle (`CompositionCycleRegressionTests.cs`), and a registration and a validation test file per provider. `OllamaHttpBoundaryFailureTests.cs` covers HTTP failure on the Ollama boundary.

## Related

Direct project references: none. Referenced by [[pages/services/conversation]], [[pages/services/host-console]]. Map: [[pages/architecture/project-map]].
