---
title: "Operator: How the rules are kept: Reply speech writes only its one"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Operator service page."
page_type: service
status: active
description: "Read when a change touches: evidence for: Adapters, LLM providers and plugins are swapped by configuration."
source: ["src/Scout.Operator/", "tests/Scout.Operator.Tests/", "context/pages/decisions/0033-channel-operator-architecture.md", "chat"]
reliability: high
---

## How the rules are kept

- Reply speech writes only its one WAV per sentence in Scout's own speech folder and deletes it once read back, also when Piper fails (`tests/Scout.Operator.Tests/Speech/ReplySpeechTests.cs:112`, `:149`), and only in an absolute folder: a relative `Scout:Speech:Directory` composes no local voice and Piper never runs (`:88`); the provider's speech, Piper and no voice are chosen in that order (`:58`, `:77`), and a failing provider call is a reason the owner can read (`:135`), also with a chosen voice (`:199`). The provider reads in the chosen voice (`:163`) or its default with none (`:176`), a voice not in its list is refused and never sent (`:187`); Piper reads with the chosen voice and its speaker (`:212`), a local sample reads with Piper even when the provider has speech (`:223`), and a local voice this machine lacks is refused before Piper runs (`:237`); the listing has the provider's voices only with speech and the local voices only with a folder (`:251`, `:263`), each speaker with its label (`:276`).

- [[pages/invariants/pluggable-boundaries]]: no gRPC type in the project, and no vendor type outside `src/Scout.Operator/FaceMouth.cs`, where `Microsoft.ML.OnnxRuntime` is used only by `OnnxRunner` behind the internal `IOnnxRunner` and `IFaceMouth`, so no ONNX type reaches the seam records, `Scout.Conversation`, `Scout.Skills` or the contracts; the model and the provider's voices are reached through Semantic Kernel only, the voice settings built inside [[pages/services/llm]] and handed over as `PromptExecutionSettings`.
