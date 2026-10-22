---
title: "ADR 0015 - Cloud LLM providers configured by API key from the desktop"
version: "1.0"
date: 2026-10-22
changes: "Initial ADR"
page_type: adr
status: accepted
description: "The AI Sources screen configures OpenAI and Gemini by API key stored in the OS keychain and passed to the worker as environment variables; subscription sign-in is not supported."
supersedes: ""
superseded_by: ""
source:
  - chat
  - ui/packages/renderer/src/app/sources/sources.component.ts
  - ui/packages/main/src/worker/build-env.ts
  - src/Scout.Llm/LlmServiceCollectionExtensions.cs
reliability: high
---

# ADR 0015 - Cloud LLM providers configured by API key from the desktop

> A cloud card on the AI Sources screen takes an API key and a chat model, keeps the key in the OS keychain, and the next worker start selects that provider through the existing `Scout.Llm` switch.

## Status

Accepted on 2026-10-22 at the owner's request. Resolves the cloud-card deferral of [[pages/decisions/0007-ai-sources-screen]] for OpenAI and Gemini.

## Context

[[pages/decisions/0007-ai-sources-screen]] drew five cloud cards and left all of them disabled until per-provider decisions landed. On 2026-10-22 the owner tried the ChatGPT Plus card, found it did nothing, and asked for a button that accepts a token. [[pages/services/llm]] already has provider factories for OpenAI, Gemini and OpenRouter, selected by `Scout:Llm:Provider`. The desktop already holds secrets in the OS keychain and hands them to the worker as environment variables ([[pages/services/desktop-ui]]).

## Decision

- **OpenAI and Gemini** cards open an inline form with an API key and a chat model. The key is stored under vault key `llm.api-key`, the model under `llm.cloud-model`, the provider under `llm.provider`. At worker start the main process maps them to `SCOUT__LLM__<PROVIDER>__APIKEY` and `__CHAT__MODEL`; Gemini also gets an explicit embeddings model, because the options default is an Ollama model name.
- **The cloud model has its own vault key.** The Local Models screen writes `llm.model` when the user downloads a local model; sharing that key would silently replace the cloud model.
- **One key slot.** Saving a different cloud provider overwrites the key. Returning to Ollama through the LLM wizard leaves the key stored but unused.
- **The renderer never reads the key back.** It can store it and ask whether one exists; the vault IPC has no get.
- **ChatGPT Plus is not supported.** A ChatGPT Plus or Pro subscription grants no API access; the only sign-in flow OpenAI offers for it is the Codex CLI's, whose token is accepted only by OpenAI's own backend and is not offered to third-party apps. The card says so instead of looking broken. This is reopened only if OpenAI publishes a sanctioned third-party flow.
- **OpenRouter and Custom API stay disabled.** The OpenRouter factory needs a separate embeddings host and key; Custom API needs endpoint validation. Each is a later change.

## Consequences

- **Local-first default holds.** With no provider saved, or `Ollama`, the worker environment is unchanged ([[pages/invariants/local-first-default]]).
- **Prompts leave the machine** once a user saves a cloud key, to the provider they chose. That is the user's explicit choice; nothing is sent before it.
- **Switching providers changes embedding dimensions.** A project index built with one provider's embeddings may not match another's dimensions; re-index after switching ([[pages/services/knowledge]]).
- **A restart is needed** for a saved key to take effect; the screen says so.

## Related

[[pages/invariants/secrets-never-in-source]], [[pages/invariants/pluggable-boundaries]], [[pages/roadmap/followups]].
