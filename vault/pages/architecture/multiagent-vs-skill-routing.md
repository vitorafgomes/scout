---
title: "Multi-agent orchestration vs. identify-the-skill (SK guidance)"
version: "1.0"
date: 2026-05-30
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: longform
status: active
description: "Source: multi-agent workflow scout-multiagent-architecture-guidance (8 agents: SK docs research via Microsoft Learn MCP -> design -> adversarial critique -> finalize)."
source:
  - "obsidian-vault/scout/02-architecture/multiagent-vs-skill-routing.md"
reliability: medium
---

# Multi-agent orchestration vs. identify-the-skill (SK guidance)

> Source: multi-agent workflow `scout-multiagent-architecture-guidance` (8 agents: SK docs research via Microsoft Learn MCP → design → adversarial critique → finalize). Question: should Scout have a specialized agent per request type (code-analyst, kubernetes-expert), via multi-agent orchestration OR skill identification?

## Verdict
**It's a false binary.** The right answer is the middle the question didn't name: **one agent, many personas, deterministic embedding routing** (Pattern B). For Scout today (local ~9B via Ollama, one GPU, streaming-first, SK-as-hub) do **NOT** build multi-agent triage/handoff orchestration. Reserve it for a frontier/much-larger model.

## The spectrum (4 patterns)
- **A) One agent + many tools — current Scout.** Shared Kernel aggregates all plugins; `FunctionChoiceBehavior.Auto()`. Cons: exactly ONE persona (Project Expert owns every turn once a project is configured); the agent path discards `request.SystemPrompt`; the model sees ~11 tools at once (search_project, find_symbol, list_skills, load_skill + 7 GitHub) — past the "7B confused above ~3 tools, degrades past 5-10" ceiling. 1 LLM call/turn.
- **B) One agent + dynamic persona/tool-scope injection — RECOMMENDED.** One `ChatCompletionAgent` on the one Kernel, persona driven per-invocation via a **templated** instruction (`PromptTemplateConfig` `{{$persona}}` + `AgentInvokeOptions.KernelArguments`) and `AgentInvokeOptions.AdditionalInstructions` (appends a one-shot constraint). Routing = the **embedding classifier already built** (`SkillSelector`/`VectorMath.Cosine`). A "specialist" = a skill-like markdown record (persona body + preferred tool list). 1 LLM call/turn + one ~free local embedding. Fixes BOTH current bugs (persona monopoly + dropped system prompt).
- **C) Multi-agent triage/handoff — `Microsoft.SemanticKernel.Agents.Orchestration` (prerelease, SKEXP0110) + `Runtime.InProcess`.** `HandoffOrchestration(handoffs, triage, specialists...)`. The handoff decision is itself a tool call. **Disqualifying for Scout now:** ≥2 LLM calls/turn + re-prefill per hop on one GPU; leans hardest on multi-turn multi-agent tool selection — exactly where open-weight models fall off a cliff. Experimental, breaks across versions.
- **D) Agent-as-plugin** — specialist wrapped as a tool. First-class only in the **separate Microsoft Agent Framework** (`Microsoft.Agents.AI`, `AsAIFunction()` — not SK; pulling it in = whole new framework). Classic SK = hand-wrap with `KernelFunctionFactory`. Nested full agent invocation per delegation (latency stacks like C); `AsAIFunction` is **not concurrency-safe** (bad under Discord's concurrent traffic); breaks streaming at the boundary.

## Why B for Scout
1. **Local model can't pay for C/D** — they turn routing into a *tool-calling* problem then add nested inferences; open-weight multi-turn tool-calling is far below frontier. B moves routing OUT of the model into a deterministic cosine classifier → a misroute is a tunable threshold, not a coin-flip.
2. **Latency on one GPU punishes hops** — B has ZERO extra hops (one embedding + one streamed call, same as today).
3. **Max reuse** — `SkillSelector`/`VectorMath.Cosine` (router), `SkillFileLoader` (specialist format), the single `ChatCompletionAgent` (→ templated), `SkillPromptComposer` (already composes the per-turn prompt; B just makes the agent path honor it). No new packages, no runtime, no SKEXP0110.

## A "specialist" (Pattern B)
Markdown like a skill, in a `specialists/` folder:
```
---
name: kubernetes-specialist
description: Kubernetes, clusters, pods, manifests, CrashLoopBackOff, kubectl, helm.
tools: []
---
You are a Kubernetes operations specialist... Do NOT call code-search tools...
```
`description` = embedding routing signal (load-bearing). `tools` = scoped set. A `general-assistant` (tools: []) is the fallback.

## Implementation / migration (content-preserving — nothing wasted on the upgrade to C)
- **Step 0 (smallest, also a bug fix — IN PROGRESS):** `ChatGateway.StreamAgentAsync` stop discarding `request.SystemPrompt`; feed `SkillPromptComposer` output via `AgentInvokeOptions.AdditionalInstructions` instead of `options: null`. Shippable alone. = the Skills "gate fix".
- **Step 1:** convert `ProjectExpertAgentFactory.Create` to a **templated** persona agent (`PromptTemplateConfig` `Template="{{$persona}}"`, `KernelPromptTemplateFactory`), single default persona. Behavior-neutral.
- **Step 2 (steady state):** add 2-3 `specialists/*.md` (code-analyst, kubernetes-specialist, general-assistant) + `SpecialistSelector` (clone of `SkillSelector`); route by embedding in `LlmStreamingRouter.Route`, swap persona via `KernelArguments["persona"]`; **log chosen specialist + cosine score every turn** (routing is a unit-testable seam; the log is how you tune `SimilarityThreshold`). Soft tool scoping via instructions.
- **Step 3 (optional):** hard tool scoping via `FunctionChoiceBehavior.Auto(functions: subset)` if logs show out-of-scope tool picks.
- **Step 4 (future, only on a model upgrade):** graduate to `HandoffOrchestration` — each `specialists/*.md` maps ~1:1 to a `ChatCompletionAgent`. Pin prerelease, suppress SKEXP0110, hard `GetValueAsync(TimeSpan)`, set `MaximumAutoInvokeAttempts` explicitly (FunctionChoiceBehavior default 5; old ToolCallBehavior default 128 — don't conflate).

## What to AVOID on a local model
1. No `GroupChat`/`Magentic` orchestration (frontier-only). 2. No deep handoff chains (cap depth 1-2). 3. No persona seeing >~3-5 tools (today ~11 with project+GitHub). 4. Route with embeddings, not an LLM triage call (only fall back to cheap LLM classify below threshold). 5. Agent-as-plugin wrappers aren't concurrency-safe. 6. **Streaming+function-calling isn't guaranteed** — Scout uses the OpenAI-compat `/v1` connector precisely because the native Ollama C# connector lacks function-calling; validate tool-call streaming empirically, keep a non-streaming fallback for the tool leg. 7. Don't mutate the shared agent's `Instructions` mid-flight (use per-invocation `KernelArguments`/`AdditionalInstructions`). 8. Don't sub-Q4 quantize the tool path (use Q8+ KV-cache). 9. **Validate the model tag** — "qwen3.5:9b" likely isn't real; tool-capable families are `qwen2.5` (7B/14B, no 9B) and `qwen3` (8B/14B/32B). Verify it exists, is tool-capable, and streams tool calls over `/v1`.

See project_scout_skills_system (private note), skills-subsystem-improvement-roadmap (private note), project_scout_function_calling_connector (private note).
