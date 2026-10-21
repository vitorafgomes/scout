---
title: "Capability Engine - unified persona + skill routing (Pattern B)"
version: "1.0"
date: 2026-05-30
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: longform
status: active
description: "Unified persona and skill routing (Pattern B): one capability engine that retrieves personas and skills from a typed markdown library, superseding the earlier skills roadmap and multi-agent proposal."
source:
  - "obsidian-vault/scout/02-architecture/capability-engine-architecture.md"
reliability: medium
---

# Capability Engine - unified persona + skill routing (Pattern B)

> Consolidates and supersedes skills-subsystem-improvement-roadmap (private note) and [[pages/architecture/multiagent-vs-skill-routing]]. The insight: **skills and specialists/personas are the same mechanism** — retrieval over a typed markdown library — applied to two layers of context. One engine, not two.

## The unification
A "capability" is a markdown file with frontmatter `type: persona | skill`, `name`, `description` (retrieval key), optional `tools`, and a body.
- **persona** (= specialist) — WHO answers + tool posture. Selected **top-1** above threshold (else `general-assistant` fallback). Injected via the agent's `{{$persona}}` template (`AgentInvokeOptions.KernelArguments["persona"]`).
- **skill** (= guidance) — HOW to reason. Selected **top-N** above threshold. Injected via `AgentInvokeOptions.AdditionalInstructions` (the gate fix).

Both share ONE loader, ONE embedding index, ONE cosine pass per turn, ONE config, ONE lifecycle, ONE log line.

### Unification proof (before → after)
| | 2 selectors | **unified** |
|---|---|---|
| embedding indexes | 2 | **1** |
| message embeds/turn | 2 | **1** |
| index-build embed passes | 2 | **1** batch over all `name+description` |
| config sections | 2 | **1** `Scout:Capabilities` |
| selectors / lifecycle stores / log lines | 2 | **1** |

The engine: one `Lazy<Task<index>>` over ALL capability descriptions → one cosine pass over the single message embedding → partition ranked results by `type`.

## Foundation (already landed)
- **Gate fix (`d61f785`)**: `ChatGateway.StreamAgentAsync` now passes the composed prompt via `AgentInvokeOptions.AdditionalInstructions` (the real SK 1.77.0 type — `ChatCompletionAgentInvokeOptions` does NOT exist). Before this, the agent path discarded `request.SystemPrompt` every turn (skills + persona were dead on the live config). This is Step 0.

## SK 1.77.0 API (verified against resolved assemblies)
`AgentInvokeOptions` (settable `KernelArguments` + `AdditionalInstructions`); `ChatCompletionAgent(PromptTemplateConfig, IPromptTemplateFactory)` ctor; `KernelPromptTemplateFactory` (Core); `PromptTemplateConfig.SemanticKernelTemplateFormat`; `Agent.Arguments` get/set, per-invoke `KernelArguments` override the defaults via `RenderInstructionsAsync`.

## Design
- **`ChatRequest`**: add `string? Persona` (the persona body). `SystemPrompt` carries composed skill text. Agent path: `Persona → KernelArguments["persona"]`, `SystemPrompt → AdditionalInstructions`. Direct path: both fold into one system message.
- **`ProjectExpertAgentFactory`**: templated agent `Template = "{{$persona}}"`, default `Arguments["persona"] = Instructions` (behavior-neutral until the router overrides).
- **Engine** (refactor `SkillSelector` → `CapabilitySelector`): `Capability(Type, Name, Description, Body, SourcePath, Tools)`; `CapabilitySelection(Persona, Skills)`; one lazy index; never throws → fallback persona + empty skills. `SkillFileLoader` extended to read `type` (default `skill`, back-compat) + `tools`. `SkillCatalog`/`SkillsPlugin` (`list_skills`/`load_skill`) filter to `Type==Skill` so personas aren't surfaced as skills.
- **Composer**: `SkillPromptComposer.Compose(basePrompt, selection.Skills)` → `SystemPrompt`; `selection.Persona.Body` → `Persona`.
- **DI**: one `Scout:Capabilities` section; one selector; cycle-safe (embedder from provider factory, not the kernel; selector not a KernelPlugin); lazy index (no build-time IO; smoke-test guard holds). Keep assembly name `Scout.Skills` (rename = churn, no gain).
- **Files shipped** under `capabilities/`: personas `general-assistant` (=base persona, fallback), `code-analyst`, `kubernetes-specialist`; the 7 existing skills retyped `type: skill`.

## Tool scoping
- **Now (soft):** persona body instructs which tools to use/avoid. Zero plumbing.
- **Later (hard):** `FunctionChoiceBehavior.Auto(functions: subset)` from the selected `Capability.Tools` — addresses the ~11-tool overload on a 9B model.

## Fold-ins (now apply to the single engine)
nomic asymmetric prefixes (`search_query:`/`search_document:`, applied at the selector call site — the connector returns a raw `IEmbeddingGenerator`); embed `name+description` not description-alone; card-vs-full-body injection mode; structured delimiter; one per-turn log line (persona+score, skills+scores) — the tunability seam; future hot-reload (one store to invalidate).

## Implementation order (TDD, ship green each step)
- **Step 1** (behavior-neutral): templated persona agent + `ChatRequest.Persona` + gateway both paths. Default persona == `ProjectExpertAgentFactory.Instructions`.
- **Step 2** (unified engine): `SkillFileLoader`→`Capability`; `SkillSelector`→`CapabilitySelector`/`ICapabilitySelector`/`CapabilitySelection`; migrate Skills tests; one `Scout:Capabilities` config; router swaps `ISkillSelector`→`ICapabilitySelector`; ship `capabilities/*.md`.
- **Step 3** (later, optional): hard tool scoping per persona.
- **Step 4** (future, only on a model upgrade): graduate to `HandoffOrchestration` — each persona maps ~1:1 to a `ChatCompletionAgent`; nothing wasted.

## What to avoid on a local model
(see [[pages/architecture/multiagent-vs-skill-routing]]) no GroupChat/Magentic; no deep handoff chains; ≤3-5 tools per persona; route with embeddings not LLM triage; validate the model tag (`qwen3.5:9b` likely isn't real — qwen2.5=7B/14B, qwen3=8B/14B/32B); confirm tool-call streaming over the `/v1` connector.
