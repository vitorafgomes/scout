---
title: "ADR 0048 - Claude as an AI Source"
version: "1.0"
date: 2026-11-07
changes: "Initial ADR"
page_type: adr
status: accepted
description: "AI Sources gets a Claude API card: the owner's Anthropic API key, the official Anthropic C# SDK behind Semantic Kernel as one more provider, models listed from the Models API, usage counted from each answer. A Claude Pro or Max subscription sign-in is not offered, because Anthropic does not allow third-party apps to offer claude.ai login without its approval."
supersedes: ""
superseded_by: ""
source:
  - chat
  - https://code.claude.com/docs/en/agent-sdk/overview
reliability: high
---

# ADR 0048 - Claude as an AI Source

> Claude joins the AI Sources the way every other provider does: a key the owner pastes, one more factory behind Semantic Kernel. A subscription sign-in is not offered.

## Status

- [x] Proposed
- [x] Accepted

Updated: 2026-11-07. The owner, translated: "we have to put the Claude Plan and the Claude API in AI Sources", "two more cards".

## Context

Scout reaches every model through Semantic Kernel ([[pages/decisions/0012-llm-communication-through-semantic-kernel]], [[pages/invariants/pluggable-boundaries]]). The ChatGPT plan works through OpenAI's own Sign in with ChatGPT. Anthropic's documentation says, verbatim: "Unless previously approved, Anthropic does not allow third party developers to offer claude.ai login or rate limits for their products" (Agent SDK overview, read 2026-11-07). Scout has no such approval.

## Decision

- **Claude API card.** The owner pastes an Anthropic API key; it is stored in the vault like the OpenAI and Gemini keys, main-only, and reaches the worker through configuration binding only ([[pages/invariants/secrets-never-in-source]]).
- **One more provider, `Anthropic`.** A factory in `Scout.Llm` builds the official Anthropic C# SDK client (NuGet `Anthropic`), uses its `Microsoft.Extensions.AI` `IChatClient` and hands it to the kernel as a chat completion service, so the conversation, function calling, the write gate and the read-only guard see the same kernel as every other provider. No Anthropic SDK type leaves the factory.
- **Models.** The card and Models → Analysis list the models the key can use from the Anthropic Models API (`GET /v1/models`), a declared read by main when the screen opens. The default is `claude-opus-5-5`.
- **Embeddings.** Anthropic has no embeddings endpoint; with Claude the project index answers from full-text search, and vectors come from Ollama when the owner has it, as with the ChatGPT plan.
- **Usage.** Each answer's `usage` (input and output tokens) reaches the usage count of [[pages/decisions/0046-ai-source-usage-on-the-cards]]; the card's balance reads "Not reported by the provider" with a link to the Anthropic Console usage page.
- **Claude Plan.** Not offered. The AI Sources screen shows no Claude Plan card; the Claude API card's help line says a Claude Pro or Max subscription cannot be used by other apps and an API key is needed. If Anthropic approves Scout later, a superseding ADR adds it.

## Consequences

- **Better:** the owner can run Scout on Claude with a key, through the same path, tests and guard as every provider.
- **Worse:** one more SDK dependency and factory; Claude has no embeddings, so semantic search on Claude needs Ollama.
