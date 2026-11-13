---
title: "Llm: How the rules are kept"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Llm service page."
page_type: service
status: active
description: "Read when a change touches: the evidence for a rule of this project: the code and the tests that hold it."
source: ["src/Scout.Llm/", "tests/Scout.Llm.Tests/"]
reliability: high
---

## How the rules are kept

- [[pages/invariants/pluggable-boundaries]]: the provider switch is the swap point; the list is closed.
- [[pages/invariants/local-first-default]]: blank provider resolves to Ollama (`src/Scout.Llm/LlmServiceCollectionExtensions.cs:62`); `None` is only ever an explicit value.
- [[pages/invariants/secrets-never-in-source]]: every `ApiKey` defaults to empty (`src/Scout.Llm/Configuration/LlmOptions.cs:71`); the plain-http warning carries the provider, the scheme and the host only (`tests/Scout.Llm.Tests/Providers/PlainHttpEndpointWarningTests.cs:74`).
- [[pages/invariants/no-executive-writes]]: the live translator offers no function (settings with a 400-token cap and no function choice, no kernel, `src/Scout.Llm/LiveTranslator.cs:60`), so it runs outside the read-only guard; the gateway offers each turn only the functions its request declares, on a per-turn kernel (`src/Scout.Llm/KernelScope.cs:19`), and a call to anything else never runs and is logged ([[pages/decisions/0054-functions-scoped-per-skill-and-job]]); the optional read-only guard judges the request, and a function declared as a write is gated at the call by the control plane (`src/Scout.Llm/Guard/WriteGateFilter.cs:37`), on the per-turn copy too (`tests/Scout.Llm.Tests/Guard/WriteGateCompositionTests.cs:55`).
- [[pages/decisions/0032-per-feature-write-permissions]]: without a granted write feature a write intent is refused naming the Features screen (`tests/Scout.Llm.Tests/Guard/ReadOnlyGuardChatCompletionServiceTests.cs:277`); with one the request passes (`tests/Scout.Llm.Tests/Guard/ReadOnlyGuardChatCompletionServiceTests.cs:292`), and the grant binds from `Scout:Features:GrantedWrites` (`tests/Scout.Llm.Tests/Guard/ReadOnlyGuardCompositionTests.cs:148`). The one write function is the operator's `youtube_make_video`, which saves a project in the control plane's own state; every other plugin is GET-only. A declared write runs only on the control plane's `allowed` (`tests/Scout.Llm.Tests/Guard/WriteGateFilterTests.cs:58`), never on `queued` or `refused` (`:80`, `:92`) or when the gate fails (`:105`).
