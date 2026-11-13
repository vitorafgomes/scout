---
paths:
  - "src/Scout.Llm/**"
  - "tests/Scout.Llm.Tests/**"
---

# Llm

Owns provider selection, the shared `Kernel` (chat completion, embeddings, every registered plugin) and automatic function calling. Does not own prompts or history ([[pages/services/conversation]]) nor the plugins themselves ([[pages/services/plugin-github]], [[pages/services/skills]], [[pages/services/knowledge]]).

## Rules this project must keep

- **Adapters, LLM providers and plugins are swapped by configuration.** Here: the provider switch is the swap point; the list is closed.
- **Ollama is the default LLM provider.** Here: blank provider resolves to Ollama; `None` is only ever an explicit value.
- **Secrets never live in source.** Here: every `ApiKey` defaults to empty; the plain-http warning carries the provider, the scheme and the host only.
- **Scout performs descriptive writes only.** Here: the live translator offers no function (settings with a 400-token cap and no function choice, no kernel, `src/Scout.Llm/LiveTranslator.cs:60`), so it runs outside the read-only guard; the gateway offers each turn only the functions its request declares, on a per-turn kernel.
- **ADR 0032 - Features declare what they write, and the owner grants each one.** Here: without a granted write feature a write intent is refused naming the Features screen; with one the request passes, and the grant binds from `Scout:Features:GrantedWrites`.

The full statement of each, with the code and tests that hold it: `context/pages/services/llm/how-the-rules-are-kept.md`.

## Pages that govern this project

Read one when your change touches what its title names.

- [[pages/invariants/pluggable-boundaries|Adapters, LLM providers and plugins are swapped by configuration]]
- [[pages/invariants/local-first-default|Ollama is the default LLM provider]]
- [[pages/invariants/secrets-never-in-source|Secrets never live in source]]
- [[pages/invariants/no-executive-writes|Scout performs descriptive writes only]]
- [[pages/decisions/0032-per-feature-write-permissions|ADR 0032 - Features declare what they write, and the owner grants each one]]
- [[pages/decisions/0035-screenshot-attached-to-a-question|ADR 0035 - A screenshot attached to a question: one JPEG on the ask line, an image part for a provider that can see, a plain sentence otherwise]] - a picture sent with the user message
- [[pages/decisions/0034-live-transcription-on-the-local-adapter|ADR 0034 - Live transcription on the local adapter: PCM16 chunks in, partial and final text out]] - amendment "live translation": a live final line translated by the connected provider
- [[pages/decisions/0048-claude-as-an-ai-source|ADR 0048 - Claude as an AI Source]] - the Claude API as one more provider, `Anthropic`
- [[pages/conventions/testing|Testing]]

## What to read for a change

Find the row your change matches and read that page. Read one or two rows, not the table.

| If the change touches | Read |
|---|---|
| Entry point; Consumer abstraction; mermaid; Turn attachments; Read outcome; Activity filter | `context/pages/services/llm/gateway.md` |
| Functions per turn | `context/pages/services/llm/function-calls.md` |
| Provider seam; Provider OpenRouter; Provider Mistral; Provider HuggingFace; Provider None | `context/pages/services/llm/providers.md` |
| Configuration keys; A key over plain http; External boundaries | `context/pages/services/llm/configuration.md` |
| Provider Ollama | `context/pages/services/llm/provider-ollama.md` |
| Provider Anthropic | `context/pages/services/llm/provider-anthropic.md` |
| Write gate seam; Read-only guard; Write gate | `context/pages/services/llm/writes-and-guard.md` |
| Media seam; Live translation seam; A picture on the request; Media capability matrix | `context/pages/services/llm/media-and-translation.md` |
| Usage seam; Usage count | `context/pages/services/llm/usage.md` |
| the evidence for a rule of this project: the code and the tests that hold it | `context/pages/services/llm/how-the-rules-are-kept.md` |
| Hot spots | `context/pages/services/llm/hot-spots.md` |
| Testing footprint | `context/pages/services/llm/testing-footprint.md` |

For where a type lives or who calls it, ask the code graph (`graphify explain "<Type>"`, `graphify affected "<Type>"`).
