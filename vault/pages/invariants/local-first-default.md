---
title: "Ollama is the default LLM provider"
version: "1.1"
date: 2026-10-21
updated: 2026-10-23
changes: "Blank-provider fallback now tested; the desktop may write None explicitly; with no TypeSafe key the kernel keeps the provider's own chat service; no allowed users and no owner credential means open"
page_type: invariant
status: active
description: "With no provider configured Scout selects Ollama on localhost, so a fresh clone runs without paid credentials. A blank or absent provider is locked to Ollama by a test; None is only ever an explicit choice."
enforced_by:
  - "src/Scout.Llm/Configuration/LlmOptions.cs:17"
  - "src/Scout.Llm/LlmServiceCollectionExtensions.cs:57"
  - "src/Scout.Llm/Configuration/LlmOptions.cs:224"
  - "src/Scout.Host.Console/appsettings.json:10"
verified_by:
  - "tests/Scout.Llm.Tests/NoneLlmProviderTests.cs:103"
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
- **Does not forbid an explicit `None`.** Since 2026-10-23 the desktop app writes `Scout:Llm:Provider = None` when the user disconnects the last cloud provider, and never falls back to Ollama on its own; Ollama becomes an explicit choice there (the Ollama card's Use action). `None` answers with a fixed message and calls no model ([[pages/services/llm]], [[pages/decisions/0017-provider-connect-disconnect]]). The invariant still holds where it applies: a blank or absent provider means Ollama in `Scout.Llm`, and the desktop still maps an unset `llm.provider` to Ollama (`ui/packages/main/src/worker/build-env.ts:39`) while passing `None` through as written (`:75`).
- **Allowed users stay open by default.** With no `Scout:Access` configuration the conductor answers everyone, as before ([[pages/decisions/0021-allowed-users]]): an empty list refuses only when `RequireAllowList` is set, and the desktop sets it only when GitHub, Kubernetes or the ChatGPT plan is connected (`ui/packages/main/src/worker/build-env.ts:58`). Locked by `tests/Scout.Conversation.Tests/ConversationConductorAccessTests.cs:94` and `ui/packages/main/test/worker-build-env.test.ts:496`.
- **Does not cover:** model availability. The user installs Ollama and pulls the chat and embedding models; the control plane can do this from the desktop UI (`src/Scout.Host.ControlPlane/Services/ModelsGrpcService.cs:22`).

## Where it is enforced

- `src/Scout.Llm/Configuration/LlmOptions.cs:17` defaults `Provider` to `"Ollama"`. `:224` defaults the endpoint to localhost; `:238` and `:246` default the chat and embedding models.
- `src/Scout.Llm/LlmServiceCollectionExtensions.cs:57` reads `Scout:Llm:Provider`; `:58` to `:61` replace a blank value with the `LlmOptions` default before the switch at `:71`.
- The read-only guard is off unless `Scout:Guard:TypeSafe:ApiKey` is set (`src/Scout.Llm/LlmServiceCollectionExtensions.cs:63`), so a fresh clone sends nothing to TypeSafe ([[pages/decisions/0016-jev-read-only-guard]]).
- The committed host settings name Ollama explicitly (`src/Scout.Host.Console/appsettings.json:10`, committed content).
- `src/Scout.ControlPlane/Providers/OllamaProviderOptions.cs:12` repeats the localhost endpoint for the control plane.

## How it is verified

`tests/Scout.Llm.Tests/NoneLlmProviderTests.cs:103` calls `AddScoutLlm` with `Scout:Llm:Provider` absent, empty and whitespace and asserts the factory is `Ollama`. Other tests that build the LLM graph set `Scout:Llm:Provider` to `Ollama` explicitly (`tests/Scout.Llm.Tests/LlmServiceCollectionExtensionsTests.cs:17`, `tests/Scout.Host.Console.Tests/HostCompositionSmokeTests.cs:48`). `tests/Scout.Llm.Tests/LlmServiceCollectionExtensionsTests.cs:78` locks that with no TypeSafe key the kernel's chat service is the provider's own, and `ui/packages/main/test/worker-build-env.test.ts:162` and `:181` lock the Ollama worker environment.

## Known gaps

- Nothing prevents a developer from committing a hosted provider as the default; `src/Scout.Host.Console/appsettings.json:10` is protected only by review.

## Related

- Services: [[pages/services/llm]], [[pages/services/host-console]], [[pages/services/control-plane]]
- Similar: [[pages/invariants/pluggable-boundaries]], [[pages/invariants/secrets-never-in-source]]

## Change protocol

To change the default, draft a superseding ADR, update `LlmOptions`, the committed settings and the README together, and add the fallback test first.
