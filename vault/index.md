---
title: "Scout Vault Index"
version: "1.1"
date: 2026-10-21
changes: "Added the Desktop UI service page and the control-plane client initiative on 2026-10-22"
page_type: index
status: active
description: "Content catalogue for the Scout LLM wiki."
updated: 2026-11-06
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

- [[pages/services/adapter-discord|Adapter.Discord]] - Discord chat adapter: receives mentions, direct messages and voice messages, opens a thread per conversation in server channels, and streams the reply into embeds that continue across messages.
- [[pages/services/adapter-slack|Adapter.Slack]] - Slack chat adapter over Socket Mode: filters bot traffic, streams the reply by posting then updating one message.
- [[pages/services/adapter-local|Adapter.Local]] - Local adapter for the desktop's quick-ask window: reads JSON requests from the worker's stdin and streams the reply as SCOUT_LOCAL lines on stdout, as the trusted local owner.
- [[pages/services/adapter-teams|Adapter.Teams]] - Microsoft Teams adapter: the Bot Framework turn handler, translator and streaming sink; the inbound webhook is deliberately not wired in this repository.
- [[pages/services/control-plane-contracts|ControlPlane.Contracts]] - The gRPC contract between the desktop UI and the control-plane host: three proto files compiled to client and server stubs.
- [[pages/services/control-plane|ControlPlane]] - Domain logic behind the desktop UI: manage the local Ollama runtime and its models, and keep the catalog, state and credentials of optional features.
- [[pages/services/desktop-ui|Desktop UI]] - Electron app: sets up a Discord bot from its token or a Slack app from a manifest, keeps secrets in the OS keychain, spawns and supervises Scout.Host.Console, stays in the tray and can start at login, manages local models from Node.
- [[pages/services/desktop-channels|Desktop channels]] - The scout:* IPC channel catalogue of the desktop app, the pinned Whisper models and the update notice.
- [[pages/services/desktop-quick-ask|Desktop quick ask]] - The floating button, the quick-ask window, the shortcut, voice turns and the shared local conversation.
- [[pages/services/desktop-operator|Desktop operator window]] - The YouTube operator window: Production and Assembly pages over the control plane's operator state, scout-media previews, and the bundled ffmpeg and fonts.
- [[pages/services/conversation|Conversation]] - Platform-neutral conversation core: adapter and sink abstractions, the LLM streaming router with short-term memory, and the stream pump that edits a reply as tokens arrive.
- [[pages/services/host-console|Host.Console]] - The chat host executable: composes LLM, skills, conversation, knowledge, adapters and the GitHub and Kubernetes plugins, runs the adapters, or runs a one-shot project index.
- [[pages/services/host-control-plane|Host.ControlPlane]] - ASP.NET gRPC host that serves the control-plane domain to the desktop UI over a Unix socket or named pipe, guarded by a per-session token.
- [[pages/services/knowledge|Knowledge]] - Indexes configured repositories into a local SQLite vector store and answers search_project, find_symbol and list_projects tool calls from it, through a Project Expert chat agent.
- [[pages/services/llm|Llm]] - Selects one LLM provider by configuration, builds the shared Semantic Kernel, and exposes a streaming chat gateway and an embedding generator to the rest of Scout.
- [[pages/services/operator|Operator]] - The YouTube channel operator's pipeline in the worker: researches a topic, writes the script with its claims, saves each stage through the control plane and posts progress to the chat that asked.
- [[pages/services/plugin-github|Plugin.GitHub]] - Read-only GitHub plugin: four Semantic Kernel functions over a typed REST client; authenticates from a token or the desktop's token file, self-gates off when neither is set.
- [[pages/services/plugin-kubernetes|Plugin.Kubernetes]] - Read-only Kubernetes plugin: six GET-only Semantic Kernel functions over the owner's kubeconfig, only for the contexts the owner allowed; self-gates off without a path and a context.
- [[pages/services/plugin-youtube|Plugin.YouTube]] - YouTube Data API read functions (channel overview, recent videos, video stats); token file or API key.
- [[pages/services/transcription-whisper|Transcription.Whisper]] - Transcribes Discord voice messages and quick-ask WAV recordings on this machine with a local Whisper model; registered only when the model file exists.
- [[pages/services/meetings|Meetings]] - Keeps the meetings the owner chose to keep, their transcript lines and their questions, in one SQLite file under Scout's data folder, renders one as Markdown and deletes them so their text does not linger; registered only when the meetings database is configured.
- [[pages/services/skills|Skills]] - Loads markdown skill files and offers them to the model two ways: embedding-matched injection into the system prompt, and list_skills and load_skill tools.

## Invariants

- [[pages/invariants/local-first-default|Ollama is the default LLM provider]] - With no provider configured Scout selects Ollama on localhost, so a fresh clone runs without paid credentials. The fallback is coded in two places; no test exercises it.
- [[pages/invariants/no-executive-writes|Scout performs descriptive writes only]] - Scout may write content that a human or another agent reads and decides on; it never mutates code, infrastructure, configuration or data on the host. The code upholds this by omission, plus an optional model-judged guard on requests (ADR 0016).
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
- [[pages/decisions/0015-cloud-llm-providers-by-api-key|ADR 0015 - Cloud LLM providers configured by API key from the desktop]] - OpenAI and Gemini cards take an API key stored in the OS keychain; ChatGPT Plus is unsupported.
- [[pages/decisions/0016-jev-read-only-guard|ADR 0016 - TypeSafe's Jev as a read-only guard in front of the model]] - With a TypeSafe key, Jev judges each request and Scout refuses the ones that ask it to change something.
- [[pages/decisions/0017-provider-connect-disconnect|ADR 0017 - Connect and disconnect cloud LLM providers, one key per provider]] - Each cloud provider keeps its own key, checked before it is stored; disconnecting the one in use switches to another connected provider or to None, never Ollama.
- [[pages/decisions/0018-chatgpt-plan-sign-in|ADR 0018 - ChatGPT plan through Sign in with ChatGPT]] - The ChatGPT Plus card signs in through the browser; the worker answers through the Responses API inside Semantic Kernel.
- [[pages/decisions/0019-github-app-device-flow|ADR 0019 - GitHub through a read-only GitHub App and the device flow]] - Connect with GitHub through a read-only App and the device flow, or paste a token (warned when it can write); one credential at a time, held by main.
- [[pages/decisions/0020-kubernetes-from-local-kubeconfig|ADR 0020 - Kubernetes from the kubeconfig on this computer, with consent per context]] - Scout reads the owner's kubeconfig in place for the contexts the owner ticks, GET only, with no logs, secrets, exec or watch.
- [[pages/decisions/0021-allowed-users|ADR 0021 - Only allowed users can use Scout once an owner credential is connected]] - The conductor answers only the user ids listed in Settings; an empty list refuses everyone while GitHub, Kubernetes or the ChatGPT plan is connected and answers everyone otherwise; the owner can pair from chat with a one-time code in a direct message.
- [[pages/decisions/0022-discord-setup-from-the-bot-token|ADR 0022 - Discord setup from the bot token]] - The wizard sets up the Discord bot from the pasted token: reads the app, turns on Message Content, invites without OAuth.
- [[pages/decisions/0023-slack-setup-from-a-manifest|ADR 0023 - Slack is set up from a prefilled app manifest and two pasted tokens]] - The desktop opens Slack's app creation flow with Scout's Socket Mode manifest; main checks the pasted bot and app-level tokens with Slack, keeps them in main-only keys and passes them to the worker.
- [[pages/decisions/0024-voice-messages-transcribed-locally|ADR 0024 - Voice messages are transcribed locally, after the allow-list]] - A Discord voice message is downloaded and transcribed by a local Whisper model only for an allowed sender, within 25 MB and 5 minutes, and answered with a quote of what was heard.
- [[pages/decisions/0025-teams-inbound-endpoint|ADR 0025 - Teams inbound endpoint (proposed)]] - One loopback POST /api/messages route exposed by an owner-created tunnel, with the Bot Framework token check failing closed.
- [[pages/decisions/0026-live-voice-conversation|ADR 0026 - Live voice conversation (proposed)]] - Join a voice channel only on an allowed user's command, DAVE through Discord.Net, drop non-allowed audio on arrival.
- [[pages/decisions/0027-desktop-presence|ADR 0027 - Scout stays resident on the desktop: tray, keep running, start at login, notifications]] - Closing the window hides Scout to the tray and the worker keeps running; start at login with --hidden; three native notifications; no tray means the old quit-on-close.
- [[pages/decisions/0028-local-quick-ask-assistant|ADR 0028 - Quick ask: talk to Scout from the operating system, over the worker's stdio]] - A global shortcut opens a small window to type or hold Space to talk; main writes JSON lines to the worker's stdin and reads SCOUT_LOCAL lines from stdout, and the local owner skips the allow-list.
- [[pages/decisions/0029-project-site-and-releases|ADR 0029 - Project site and releases]] - Site in docs/ on Pages; v* tags publish packages.
- [[pages/decisions/0030-youtube-plugin-read-only-with-browser-sign-in|ADR 0030 YouTube plugin]] - Read-only YouTube plugin over the official APIs; browser sign-in from the desktop; key for public data.
- [[pages/decisions/0031-desktop-self-update|ADR 0031 Desktop self-update]] - The desktop replaces its own executable only on the user's click, from a checksummed release; macOS and .deb keep Download.
- [[pages/decisions/0032-per-feature-write-permissions|ADR 0032 Per-feature write permissions]] - Features declare their writes; the owner grants each one and approves irreversible actions in the desktop.
- [[pages/decisions/0033-channel-operator-architecture|ADR 0033 Channel operator architecture]] - Operator state in the control plane, the pipeline in Scout.Operator, media through the connected AI Source, progress back to the originating chat.
- [[pages/decisions/0034-live-transcription-on-the-local-adapter|ADR 0034 Live transcription on the local adapter]] - PCM16 chunks over the worker's stdin, an RMS utterance check, greedy partials dropped while busy and beam-5 finals, over the one shared Whisper model.
- [[pages/decisions/0035-screenshot-attached-to-a-question|ADR 0035 Screenshot attached to a question]] - one JPEG of at most 3 MB on the ask line, sent as an image part when the connected AI source can read images, a plain sentence without a model call otherwise, never stored or logged.
- [[pages/decisions/0036-text-read-from-a-screenshot-by-a-local-ocr-program|ADR 0036 Text read from a screenshot by a local OCR program]] - the worker runs the bundled tesseract on the JPEG bytes through standard input and output, with the app's locale languages, a 3 s budget and a 5,000-character cap, and registers the reader only when the program and its language data are present; the image and the text are never stored or logged.
- [[pages/decisions/0037-meetings-kept-locally-in-sqlite|ADR 0037 Meetings kept locally in SQLite]] - a meeting, its lines with their translations and the owner's questions are stored only when the owner chooses Keep this meeting, in one SQLite file under Scout's data folder; listed, exported to Markdown and deleted from the desktop, a delete scrubs the text with secure_delete, VACUUM and a WAL checkpoint, and nothing of it is sent anywhere or logged.
- [[pages/decisions/0038-code-quality-assistant-on-the-write-gate|ADR 0038 Code quality assistant on the write gate]] - two skills over the GitHub plugin's reads of one pull request, its comments, one issue and one file; a posted review and an opened issue as irreversible writes of the github feature, each a draft on the worker, an action the owner approves and an executor that posts it, with the App widened to Pull requests and Issues read & write; CodeQA superseded by Knowledge, src/old untouched.

## Domain

- [[pages/domain/cloud-status|`Plugin.CloudStatus`]] *(archived)* - Retired first cut under src/old. Read-only cloud health plugin. Answers questions about whether Azure, AWS, and Oracle Cloud services are healthy, degraded, or down.
- [[pages/domain/code-qa|`Plugin.CodeQA`]] *(archived)* - Retired first cut under src/old. Read-only code Q&A plugin. Answers natural-language questions about a configured GitHub repository using retrieval-augmented generation (RAG).
- [[pages/domain/daily-assistant-plugins|Daily-Assistant Plugins - Wave 1 Reference]] - What actually shipped in Wave 1 of the daily-assistant plugin roadmap (plugin-roadmap-daily-assistant (private note)): the tools the chat LLM can call, their data sources, config, and how to use...

## Conventions

- [[pages/conventions/testing|Testing]] - How Scout is tested: xUnit with Shouldly and NSubstitute, one test project per source project, warnings as errors, and the CI steps that run them.
- [[pages/conventions/tooling|Tooling]] - Who runs on which model and writes what, what lives in .claude/ and scripts/, what each hook does, and how the review workflow and the diagnostics script fit the gates.

## Runbooks

- [[pages/runbooks/local-models|Local Models - operations runbook]] - Quick reference for working with the AI Sources / Models surface in the Scout Desktop app.
- [[pages/runbooks/release|Release]] - Bump ui/package.json, tag main with v<version>, check the Release workflow, the release page and the site.

## Roadmap

- [[pages/roadmap/followups|Follow-ups]] - Deferred work found while doing something else: each entry names the blocker, the evidence and what unblocks it.
- [[pages/roadmap/chatgpt-plan-provider|ChatGPT plan provider]] - Initiative: answer through the owner's ChatGPT plan via the official browser sign-in.
- [[pages/roadmap/control-plane-client|Control plane client]] - Initiative: gRPC client in the Electron app so the Providers, Features and Models screens run through Scout.Host.ControlPlane.
- [[pages/roadmap/youtube-creator-assistant|YouTube creator assistant]] - Initiative: claude-youtube surveyed; read-only YouTube plugin with browser sign-in, dated benchmarks.
- [[pages/roadmap/desktop-auto-update|Desktop update notice and one-click update]] - Initiative: version check against GitHub Releases, what is new, user-approved update.
- [[pages/roadmap/youtube-channel-operator|YouTube channel operator]] - Initiative: research, script, narration, visuals, video, review, approved publish, learn loop, engage (comments).
- [[pages/roadmap/live-assistant|Live assistant: hear, see and translate for the owner]] - Initiative: streaming transcription, screenshot on demand, system audio, translation, OCR and a meeting session, local first and declared as reads.
- [[pages/roadmap/production-screen|Production screen: the owner's working area first]] - Initiative: the Production screen rearranged around the project the owner works on, removable presenter versions, claims at a glance, help marks on every block.
- [[pages/roadmap/teams-adapter-spike|Teams adapter spike]] - Research: Teams needs a public HTTPS endpoint; options, security and a phased plan.
- [[pages/roadmap/live-voice-spike|Live voice spike]] - Research: DAVE voice encryption, Discord.Net receive, local STT and TTS; verdict and phased plan.
- [[pages/roadmap/code-quality-assistant|Code quality assistant]] - Initiative: from chat, review a pull request and post the review on it, or find a project's improvements and open the issues, in any language, under the owner's grant and per-action approval; phases 0 to 3 done 2026-11-06, the App permissions are the owner's step.

## History

- [[pages/history/adapter-discord|Adapter.Discord - history]] - What changed in the Discord adapter and when.
- [[pages/history/adapter-slack|Adapter Slack history]] - Dated changes to the Slack adapter: the SlackNet 0.19 move to an awaited, token-bounded DisconnectAsync.
- [[pages/history/skills|Skills - history]] - What changed in the skills selector and when.
- [[pages/history/llm|Llm - history]] - What changed in the LLM gateway and when.
- [[pages/history/operator|Operator - history]] - What changed in the channel operator worker and when, and the FaceMouth acceptance run of phase 8c-B.
