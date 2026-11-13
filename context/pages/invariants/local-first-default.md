---
title: "Ollama is the default LLM provider"
version: "1.2"
date: 2026-10-21
updated: 2026-11-11
changes: "v1.2 (2026-11-11): the desktop downloads the embedding model nomic-embed-text on the owner's Download click and tells the worker to ask for that model; citations into build-env and its test re-pointed by content."
page_type: invariant
status: active
description: "With no provider configured Scout selects Ollama on localhost, so a fresh clone runs without paid credentials. A blank or absent provider is locked to Ollama by a test; None is only ever an explicit choice."
enforced_by:
  - "src/Scout.Llm/Configuration/LlmOptions.cs:17"
  - "src/Scout.Llm/LlmServiceCollectionExtensions.cs:58"
  - "src/Scout.Llm/Configuration/LlmOptions.cs:250"
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
- **Does not forbid an explicit `None`.** Since 2026-10-23 the desktop app writes `Scout:Llm:Provider = None` when the user disconnects the last cloud provider, and never falls back to Ollama on its own; Ollama becomes an explicit choice there (the Ollama card's Use action). `None` answers with a fixed message and calls no model ([[pages/services/llm]], [[pages/decisions/0017-provider-connect-disconnect]]). The invariant still holds where it applies: a blank or absent provider means Ollama in `Scout.Llm`, and the desktop still maps an unset `llm.provider` to Ollama (`ui/packages/main/src/worker/build-env.ts:72`) while passing `None` through as written (`:158`).
- **Allowed users stay open by default.** With no `Scout:Access` configuration the conductor answers everyone, as before ([[pages/decisions/0021-allowed-users]]): an empty list refuses only when `RequireAllowList` is set, and the desktop sets it only when GitHub, Kubernetes or the ChatGPT plan is connected (`ui/packages/main/src/worker/build-env.ts:106`). Locked by `tests/Scout.Conversation.Tests/ConversationConductorAccessTests.cs:94` and `ui/packages/main/test/worker-build-env.test.ts:784`.
- **Does not cover:** model availability. The user installs Ollama and pulls the chat and embedding models; the control plane can do this from the desktop UI (`src/Scout.Host.ControlPlane/Services/ModelsGrpcService.cs:17`). Since 2026-11-11 the desktop downloads the embedding model, `nomic-embed-text`, on the owner's Download click: a Download on an Ollama chat model in "Local models in Ollama" also pulls it while Ollama lacks it, and its own Skill matching row offers Download; Scout never pulls it at worker start or in the background. The desktop passes the same name to the worker as `SCOUT__LLM__OLLAMA__EMBEDDINGS__MODEL` (`ui/packages/main/src/worker/build-env.ts:155`, locked by `ui/packages/main/test/worker-build-env.test.ts:353`), so the model asked for is the model downloaded ([[pages/services/desktop-ui]], Ollama models on the Ollama card).

## Where it is enforced

- `src/Scout.Llm/Configuration/LlmOptions.cs:17` defaults `Provider` to `"Ollama"`. `:260` defaults the endpoint to localhost; `:274` and `:282` default the chat and embedding models.
- `src/Scout.Llm/LlmServiceCollectionExtensions.cs:61` reads `Scout:Llm:Provider`; `:62` to `:65` replace a blank value with the `LlmOptions` default before the switch at `:76`.
- The read-only guard is off unless `Scout:Guard:TypeSafe:ApiKey` is set (`src/Scout.Llm/LlmServiceCollectionExtensions.cs:67`), so a fresh clone sends nothing to TypeSafe ([[pages/decisions/0016-jev-read-only-guard]]).
- The committed host settings name Ollama explicitly (`src/Scout.Host.Console/appsettings.json:10`, committed content).
- `src/Scout.ControlPlane/Providers/OllamaProviderOptions.cs:12` repeats the localhost endpoint for the control plane.

## How it is verified

`tests/Scout.Llm.Tests/NoneLlmProviderTests.cs:103` calls `AddScoutLlm` with `Scout:Llm:Provider` absent, empty and whitespace and asserts the factory is `Ollama`. Other tests that build the LLM graph set `Scout:Llm:Provider` to `Ollama` explicitly (`tests/Scout.Llm.Tests/LlmServiceCollectionExtensionsTests.cs:16`, `tests/Scout.Host.Console.Tests/HostCompositionSmokeTests.cs:49`). `tests/Scout.Llm.Tests/LlmServiceCollectionExtensionsTests.cs:77` locks that with no TypeSafe key the kernel's chat service is the provider's own, and `ui/packages/main/test/worker-build-env.test.ts:373` and `:395` lock the Ollama worker environment.

## Known gaps

- Nothing prevents a developer from committing a hosted provider as the default; `src/Scout.Host.Console/appsettings.json:10` is protected only by review.

## Related

- Services: [[pages/services/llm]], [[pages/services/host-console]], [[pages/services/control-plane]]
- Similar: [[pages/invariants/pluggable-boundaries]], [[pages/invariants/secrets-never-in-source]]

## Change protocol

To change the default, draft a superseding ADR, update `LlmOptions`, the committed settings and the README together, and add the fallback test first.
