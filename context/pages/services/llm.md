---
title: "Llm"
version: "2.35"
date: 2026-10-21
updated: 2026-11-12
changes: "v2.35 (2026-11-12) the allowed answer of a reversible write carries the id of the started entry the control plane logged before it runs, and the record names it (issue #323); no worker code changed; WriteGateDecision, WriteFunctionDeclaration and SummaryArguments citations re-pointed by content."
page_type: service
status: active
description: "Selects one LLM provider by configuration, builds the shared Semantic Kernel, and exposes a streaming chat gateway and an embedding generator to the rest of Scout."
source: ["src/Scout.Llm/", "tests/Scout.Llm.Tests/"]
reliability: high
---

# Llm

> The single door to a language model: one provider, one shared kernel, one streaming gateway.

## Responsibility

Owns provider selection, the shared `Kernel` (chat completion, embeddings, every registered plugin) and automatic function calling. Does not own prompts or history ([[pages/services/conversation]]) nor the plugins themselves ([[pages/services/plugin-github]], [[pages/services/skills]], [[pages/services/knowledge]]).

## Rules

The rules this project must keep, and what to read before changing it, are in `.claude/rules/llm.md`. Claude Code loads that file when a task touches the project.

## Topics

Read the page your change touches, not all of them.

| Page | Read when a change touches | Characters |
|---|---|---|
| [[pages/services/llm/gateway\|Gateway]] | Entry point; Consumer abstraction; mermaid; Turn attachments; Read outcome; Activity filter | 5943 |
| [[pages/services/llm/function-calls\|Function calls]] | Functions per turn | 10847 |
| [[pages/services/llm/providers\|Providers]] | Provider seam; Provider OpenRouter; Provider Mistral; Provider HuggingFace; Provider None | 8488 |
| [[pages/services/llm/configuration\|Configuration and endpoints]] | Configuration keys; A key over plain http; External boundaries | 5351 |
| [[pages/services/llm/provider-ollama\|Provider: Ollama]] | Provider Ollama | 5695 |
| [[pages/services/llm/provider-anthropic\|Provider: Anthropic]] | Provider Anthropic | 5654 |
| [[pages/services/llm/writes-and-guard\|Writes and the read-only guard]] | Write gate seam; Read-only guard; Write gate | 9188 |
| [[pages/services/llm/media-and-translation\|Media and live translation]] | Media seam; Live translation seam; A picture on the request; Media capability matrix | 10844 |
| [[pages/services/llm/usage\|Usage]] | Usage seam; Usage count | 6375 |
| [[pages/services/llm/how-the-rules-are-kept\|How the rules are kept]] | the evidence for a rule of this project: the code and the tests that hold it | 2474 |
| [[pages/services/llm/hot-spots\|Hot spots]] | Hot spots | 9233 |
| [[pages/services/llm/testing-footprint\|Testing footprint]] | Testing footprint | 10710 |

## Related

Direct project references: none. Dated changes: [[pages/history/llm]]. Referenced by [[pages/services/conversation]], [[pages/services/host-console]]. Map: [[pages/architecture/project-map]].
