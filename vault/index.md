---
title: "Scout Vault Index"
version: "1.0"
date: 2026-10-21
changes: "Founded on 2026-10-21: migrated technical notes plus code-derived service, invariant and convention pages"
page_type: index
status: active
description: "Content catalogue for the Scout LLM wiki."
updated: 2026-10-21
source:
  - chat
  - obsidian-vault/scout (technical notes migrated 2026-10-21)
  - src/
  - tests/
reliability: high
---

# Scout Vault Index

The content catalogue for this vault. Start here, follow wikilinks. If the vault can't answer, grep source, then `/wiki-crystallize` the findings back.

- Operation contract: [[CLAUDE]] (how Claude operates on this vault)
- Page schema: [[wiki-schema]] | Config: [[wiki-config]]
- Templates: `templates/`

> **Coverage note (2026-10-21):** founded in one day. The pages under `services/`, `invariants/`,
> `conventions/` and `architecture/project-map` were written from the code at the founding commit
> and carry verified `file:line` citations (`reliability: high`). Everything else was migrated
> verbatim from the owner's Obsidian notes dated 2026-04 to 2026-07 and was **not re-verified
> against the code** (`reliability: medium`): a `status: stub` or `proposed` there means the source
> note was still a draft. Where a migrated page and a code-derived page disagree, the code-derived
> page is the fact and the disagreement is a finding to log. The owner's private notes (plans,
> handoffs, sessions, audits) stay outside this repo.
>
> Known gaps the founding pass recorded, for the first review: the read-only contract is a
> convention, not an enforced allow-list ([[pages/invariants/no-executive-writes]]); secrets are kept
> out of source by `.gitignore` alone, with no scanning in CI ([[pages/invariants/secrets-never-in-source]]);
> the control plane mutates the host (Ollama models, the `ollama serve` process) on the user's
> behalf and ADR 0009 does not yet classify that.


## Overview

- [[pages/overview/vision|Vision]] *(stub)* - Scout vision: a chatops assistant answering recurring operational and business questions in chat for engineering teams.

## Architecture

- [[pages/architecture/capability-engine-architecture|Capability Engine - unified persona + skill routing (Pattern B)]] - Unified persona and skill routing (Pattern B): one capability engine that retrieves personas and skills from a typed markdown library, superseding the earlier skills roadmap and multi-agent proposal.
- [[pages/architecture/desktop-models-system|Desktop models system (AI Sources → Models)]] - How the Scout Desktop pieces fit together to discover an inference backend (AI Sources), download a model into it, and select which model to use.
- [[pages/architecture/desktop-ui-design-system|Scout Desktop - Visual Interface Design Document]] - Hub for the Scout Desktop visual interface design document: principles, tokens, layout, components, screens, interaction patterns, accessibility, CSS custom properties and open questions.
  - split into 10 part pages under `pages/architecture/desktop-ui-design-system/`, linked from the hub
- [[pages/architecture/electron-provisioning-ui|Electron Provisioning UI - feasibility study]] - Hub for the feasibility study of an Electron provisioning UI that lets users set up chat platform bots without manual developer-portal steps, with per-platform limits and a recommendation.
  - split into 10 part pages under `pages/architecture/electron-provisioning-ui/`, linked from the hub
- [[pages/architecture/high-level|High-level architecture]] *(stub)* - The orchestration kernel. Hosts Semantic Kernel, loads enabled plugins and the configured LLM provider, and exposes a single HandleAsync(ChatMessage) -> ChatResponse entry point that adapters call.
- [[pages/architecture/multiagent-vs-skill-routing|Multi-agent orchestration vs. identify-the-skill (SK guidance)]] - Source: multi-agent workflow scout-multiagent-architecture-guidance (8 agents: SK docs research via Microsoft Learn MCP -> design -> adversarial critique -> finalize).
- [[pages/architecture/project-knowledge-rag|Project Knowledge RAG - Design]] *(stub)* - Design for letting a user ask Scout natural-language questions about a software project and get answers grounded in that project's real code and docs.
- [[pages/architecture/project-map|Project map]] - Every project under src/ with its kind, its ProjectReference dependencies and the test project that covers it.
- [[pages/architecture/restructure-architecture|Scout Restructure - Architecture, Status and Backlog]] - Living overview of the ground-up restructure on branch feat/scout-llm-gateway. The previous source tree is archived under src/old/ (frozen reference, not built).
- [[pages/architecture/selfinfo-plugin-survey|Self/Host-Info Plugin - Scope & Security Survey]] *(proposed)* - Scoping for a plugin (Scout.Plugin.SelfInfo, working name) that answers \"where and how is Scout installed\" - install path, version, OS, resources, active config, worker health.

## Services (one per src project)

- [[pages/services/adapter-discord|Adapter.Discord]] - Discord chat adapter: receives mentions and direct messages over the gateway, and streams the reply by posting then editing one embed.
- [[pages/services/adapter-slack|Adapter.Slack]] - Slack chat adapter over Socket Mode: filters bot traffic, streams the reply by posting then updating one message.
- [[pages/services/adapter-teams|Adapter.Teams]] - Microsoft Teams adapter: the Bot Framework turn handler, translator and streaming sink; the inbound webhook is deliberately not wired in this repository.
- [[pages/services/control-plane-contracts|ControlPlane.Contracts]] - The gRPC contract between the desktop UI and the control-plane host: three proto files compiled to client and server stubs.
- [[pages/services/control-plane|ControlPlane]] - Domain logic behind the desktop UI: manage the local Ollama runtime and its models, and keep the catalog, state and credentials of optional features.
- [[pages/services/conversation|Conversation]] - Platform-neutral conversation core: adapter and sink abstractions, the LLM streaming router with short-term memory, and the stream pump that edits a reply as tokens arrive.
- [[pages/services/host-console|Host.Console]] - The chat host executable: composes LLM, skills, conversation, knowledge, adapters and the GitHub plugin, runs the adapters, or runs a one-shot project index.
- [[pages/services/host-control-plane|Host.ControlPlane]] - ASP.NET gRPC host that serves the control-plane domain to the desktop UI over a Unix socket or named pipe, guarded by a per-session token.
- [[pages/services/knowledge|Knowledge]] - Indexes configured repositories into a local SQLite vector store and answers search_project, find_symbol and list_projects tool calls from it, through a Project Expert chat agent.
- [[pages/services/llm|Llm]] - Selects one LLM provider by configuration, builds the shared Semantic Kernel, and exposes a streaming chat gateway and an embedding generator to the rest of Scout.
- [[pages/services/plugin-github|Plugin.GitHub]] - Read-only GitHub plugin: four Semantic Kernel functions over a typed REST client; self-gates off when no token is configured.
- [[pages/services/skills|Skills]] - Loads markdown skill files and offers them to the model two ways: embedding-matched injection into the system prompt, and list_skills and load_skill tools.

## Invariants

- [[pages/invariants/local-first-default|Ollama is the default LLM provider]] - With no provider configured Scout selects Ollama on localhost, so a fresh clone runs without paid credentials. The fallback is coded in two places; no test exercises it.
- [[pages/invariants/no-executive-writes|Scout performs descriptive writes only]] - Scout may write content that a human or another agent reads and decides on; it never mutates code, infrastructure, configuration or data on the host. The code upholds this by omission, not by a gate.
- [[pages/invariants/pluggable-boundaries|Adapters, LLM providers and plugins are swapped by configuration]] - Chat adapters, LLM providers and plugins sit behind abstractions and are selected or gated by Scout: configuration; the core names no vendor. The set of options is closed at compile time.
- [[pages/invariants/secrets-never-in-source|Secrets never live in source]] - Tokens and API keys come from user-secrets in development and environment variables in production; committed appsettings ship empty values. Only .gitignore and a convention hold this; no code or test does.

## Decisions (ADRs)

- [[pages/decisions/0001-platform-priority|ADR-001 - Chat platform rollout order]] - Decision on the rollout order of chat platforms: Discord first, then Slack and Teams, to limit the surface area of the first release.
- [[pages/decisions/0002-license-mit|ADR-002 - License choice (MIT)]] - Decision to release Scout under the MIT license, and what the license choice affects.
- [[pages/decisions/0003-shouldly-migration|ADR-003 - Shouldly over FluentAssertions]] - Decision to migrate test assertions from FluentAssertions to Shouldly after FluentAssertions moved to a commercial license.
- [[pages/decisions/0004-web-stack-angular|ADR - Web stack is Angular]] - Decision to use Angular for Scout's web surfaces.
- [[pages/decisions/0005-scout-desktop-m1-scope|Scout Desktop M1 scope and deferrals]] - Plan 5 bootstrapped Scout Desktop end-to-end. This ADR captures the technical decisions made during M1 and the explicit deferrals, so post-M1 work can build on the same foundation without rediscovery.
- [[pages/decisions/0006-models-screen-task-based|Models screen restructured by task (Transcription / Analysis / Translation)]] - Decision to restructure the Models screen around tasks (Transcription, Analysis, Translation) instead of a single Ollama model picker.
- [[pages/decisions/0007-ai-sources-screen|AI Sources screen for inference backend discovery]] - Decision to add an AI Sources screen for discovering inference backends, complementing the task-based Models screen.
- [[pages/decisions/0008-local-models-real-downloads|Real downloads for local models (Ollama pull + HF Whisper GGML)]] - The first iteration of \"Manage Downloaded Models\" used mocked state: clicking Download just flipped a flag in localStorage.
- [[pages/decisions/0009-descriptive-vs-executive-writes|Descriptive vs executive writes - refining the read-only invariant]] - Refines Scout's read-only invariant by distinguishing descriptive writes from executive writes.
- [[pages/decisions/0010-scout-host-kernel-accessor|ScoutHost.ResolvedKernel accessor for dynamic plugin registration]] - Decision to add a ResolvedKernel accessor on ScoutHost to support dynamic plugin registration.
- [[pages/decisions/0011-backend-frontend-grpc-uds|Backend-frontend transport - gRPC over Unix domain socket / named pipe]] - Scout's desktop app is Electron (Node main process + Angular renderer). The .NET backend runs as a supervised child process (sidecar).
- [[pages/decisions/0012-llm-communication-through-semantic-kernel|All LLM communication routes through Semantic Kernel (Scout.Llm gateway)]] - Decision that all LLM communication in Scout routes through Semantic Kernel.
- [[pages/decisions/0013-conversation-core-streaming-sink|Conversation core - cumulative streaming sink and pump-owned drive loop]] - Decision on the conversation core's cumulative streaming sink and pump-owned streaming across chat platforms.
- [[pages/decisions/0014-per-request-identity-gating|ADR - Per-request identity and disclosure gating (SelfInfo Path B)]] - Plugins run inside a singleton Semantic Kernel with singleton KernelPlugin instances, so they had no way to know *who* was asking or *where*.

## Domain

- [[pages/domain/cloud-status|`Plugin.CloudStatus`]] *(stub)* - Read-only cloud health plugin. Answers questions about whether Azure, AWS, and Oracle Cloud services are healthy, degraded, or down.
- [[pages/domain/code-qa|`Plugin.CodeQA`]] *(stub)* - Read-only code Q&A plugin. Answers natural-language questions about a configured GitHub repository using retrieval-augmented generation (RAG).
- [[pages/domain/daily-assistant-plugins|Daily-Assistant Plugins - Wave 1 Reference]] - What actually shipped in Wave 1 of the daily-assistant plugin roadmap (plugin-roadmap-daily-assistant (private note)): the tools the chat LLM can call, their data sources, config, and how to use...

## Conventions

- [[pages/conventions/testing|Testing]] - How Scout is tested: xUnit with Shouldly and NSubstitute, one test project per source project, warnings as errors, and the CI steps that run them.
- [[pages/conventions/tooling|Tooling]] - Who runs on which model and writes what, what lives in .claude/ and scripts/, what each hook does, and how the review workflow and the diagnostics script fit the gates.

## Runbooks

- [[pages/runbooks/local-models|Local Models - operations runbook]] - Quick reference for working with the AI Sources / Models surface in the Scout Desktop app.

## Roadmap

- [[pages/roadmap/followups|Follow-ups]] - Deferred work found while doing something else: each entry names the blocker, the evidence and what unblocks it.

## History

- [[pages/history/adapter-slack|Adapter Slack history]] - Dated changes to the Slack adapter: the SlackNet 0.19 move to an awaited, token-bounded DisconnectAsync.
