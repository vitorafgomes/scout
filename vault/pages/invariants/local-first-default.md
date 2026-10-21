---
title: "Ollama is the default LLM provider"
version: "1.1"
date: 2026-10-21
updated: 2026-10-21
changes: "Re-verified against the imported development line on 2026-10-21"
page_type: invariant
status: active
description: "With no provider configured Scout selects Ollama on localhost, so a fresh clone runs without paid credentials. The fallback is coded in three places (two in .NET, one in the desktop app); no test exercises the .NET one."
enforced_by:
  - "src/Scout.Llm/Configuration/LlmOptions.cs:16"
  - "src/Scout.Llm/LlmServiceCollectionExtensions.cs:48"
  - "src/Scout.Llm/Configuration/LlmOptions.cs:207"
  - "src/Scout.Host.Console/appsettings.json:10"
  - "ui/packages/main/src/worker/build-env.ts:43"
verified_by: []
source:
  - "src/Scout.Llm/Configuration/LlmOptions.cs"
  - "src/Scout.Llm/LlmServiceCollectionExtensions.cs"
  - "src/Scout.Host.Console/appsettings.json"
  - "tests/Scout.Llm.Tests/LlmServiceCollectionExtensionsTests.cs"
reliability: high
---

# Ollama is the default LLM provider

> When `Scout:Llm:Provider` is absent, empty or whitespace, Scout uses the Ollama provider against `http://localhost:11434`, so no API key or paid account is needed to run it.

## Why this exists

An open-source assistant that needs a paid key before it starts loses contributors on the first run. Local inference keeps prompts and repository content on the user's machine.

## Scope

- **Applies to:** `Scout.Llm` provider selection and the committed host settings.
- **Does not cover:** model availability. The user installs Ollama and pulls the chat and embedding models; the control plane can do this from the desktop UI (`src/Scout.Host.ControlPlane/Services/ModelsGrpcService.cs:22`).

## Where it is enforced

- `src/Scout.Llm/Configuration/LlmOptions.cs:16` defaults `Provider` to `"Ollama"`. `:207` defaults the endpoint to localhost; `:221` and `:229` default the chat and embedding models.
- `src/Scout.Llm/LlmServiceCollectionExtensions.cs:48` reads `Scout:Llm:Provider`; `:49` to `:52` replace a blank value with the `LlmOptions` default before the switch at `:54`.
- The committed host settings name Ollama explicitly (`src/Scout.Host.Console/appsettings.json:10`, committed content) and pick the chat model `qwen3.5:9b` (`:14`), which differs from the code default `llama3.1`.
- The desktop app also falls back to Ollama when no provider is stored in its vault (`ui/packages/main/src/worker/build-env.ts:43`) and passes the choice to the worker as `SCOUT__LLM__PROVIDER` (`:65`).
- Seven other providers (OpenAI, OpenRouter, AzureOpenAI, Mistral, HuggingFace, AzureAIInference, Gemini) are one config value away (`src/Scout.Llm/LlmServiceCollectionExtensions.cs:56` to `:79`); none is the default.
- `src/Scout.ControlPlane/Providers/OllamaProviderOptions.cs:12` repeats the localhost endpoint for the control plane.

## How it is verified

The fallback itself is not tested. Every test that builds the LLM graph sets `Scout:Llm:Provider` to `Ollama` explicitly (`tests/Scout.Llm.Tests/LlmServiceCollectionExtensionsTests.cs:16`, `tests/Scout.Host.Console.Tests/HostCompositionSmokeTests.cs:49`), including the case named `EmptyConfig` (`:72`), which still carries the core Ollama keys. `tests/Scout.Llm.Tests/LlmServiceCollectionExtensionsTests.cs:38` proves Ollama resolves, not that it is chosen when nothing is set.

## Known gaps

- Add a test that calls `AddScoutLlm` with an empty configuration and asserts the factory name is `Ollama`.
- Nothing prevents a developer from committing a hosted provider as the default; `src/Scout.Host.Console/appsettings.json:10` is protected only by review.

## Related

- Services: [[pages/services/llm]], [[pages/services/host-console]], [[pages/services/control-plane]]
- Similar: [[pages/invariants/pluggable-boundaries]], [[pages/invariants/secrets-never-in-source]]

## Change protocol

To change the default, draft a superseding ADR, update `LlmOptions`, the committed settings and the README together, and add the fallback test first.
