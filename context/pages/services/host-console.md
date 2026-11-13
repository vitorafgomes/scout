---
title: "Host.Console"
version: "1.24"
date: 2026-10-21
updated: 2026-11-11
changes: "The index command stops at its next await on Ctrl+C or SIGTERM through the host's lifetime, without starting the host's services, and exits 1 (2026-11-11).."
page_type: service
status: active
description: "The chat host executable: composes LLM, skills, conversation, knowledge, adapters (the local quick-ask one included), the GitHub, Kubernetes and YouTube plugins and the YouTube channel operator, runs the adapters, or runs a one-shot project index."
source: ["src/Scout.Host.Console/", "tests/Scout.Host.Console.Tests/"]
reliability: high
---

# Host.Console

> The process the desktop app launches and supervises: it starts whichever adapters are configured and answers until stopped.

## Responsibility

Owns composition, the conversation worker, the readiness marker, the parent watchdog and the `index` command. Does not own behaviour of any piece it wires.

## Rules

The rules this project must keep, and what to read before changing it, are in `.claude/rules/host-console.md`. Claude Code loads that file when a task touches the project.

## Topics

Read the page your change touches, not all of them.

| Page | Read when a change touches | Characters |
|---|---|---|
| [[pages/services/host-console/public-contract-entry-point\|Public contract: Entry point]] | Entry point; Composition; Runtime types | 9042 |
| [[pages/services/host-console/public-contract-configuration-keys-read-here\|Public contract: Configuration keys read here]] | Configuration keys read here; External boundaries | 8600 |
| [[pages/services/host-console/how-it-works\|How it works]] | How it works | 4030 |
| [[pages/services/host-console/hot-spots\|Hot spots]] | Hot spots | 742 |
| [[pages/services/host-console/testing-footprint\|Testing footprint]] | Testing footprint | 10760 |

## Related

Direct project references: [[pages/services/llm]], [[pages/services/control-plane-contracts]] (the `WorkerGate` client stubs; its `Grpc.AspNetCore` package brings the ASP.NET Core shared framework into the worker), [[pages/services/conversation]], [[pages/services/knowledge]], [[pages/services/transcription-whisper]], [[pages/services/meetings]], [[pages/services/adapter-discord]], [[pages/services/adapter-slack]], [[pages/services/adapter-teams]], [[pages/services/adapter-local]], [[pages/services/plugin-github]], [[pages/services/plugin-kubernetes]], [[pages/services/plugin-youtube]], [[pages/services/operator]]. Skills arrives through Conversation. The test project also references [[pages/services/control-plane]], for the catalog the declared writes are checked against, and [[pages/services/host-control-plane]], for the 16 MB round trip against the real host. Map: [[pages/architecture/project-map]].
