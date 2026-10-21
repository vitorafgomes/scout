---
title: "Vision"
version: "1.0"
date: 2026-04-21
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: overview
status: stub
description: "Scout vision: a chatops assistant answering recurring operational and business questions in chat for engineering teams."
source:
  - "obsidian-vault/scout/01-overview/vision.md"
reliability: medium
---

# Vision

## Problem

Engineering teams spend time answering recurring operational and business questions in chat:

- "Is prod Azure up?"
- "What is the status of our AWS RDS cluster?"
- "How is the checkout flow implemented in the monolith?"
- "Which service owns this endpoint?"

These questions have deterministic answers that live in cloud provider status APIs, source repositories, and pipelines — but reaching them requires context, access, and often a developer as intermediary.

## What Scout is

A chat bot that:

1. Runs inside the team's existing chat platform (Slack, Discord, or Teams — one at a time, any combination over time).
2. Accepts natural-language or slash-command queries.
3. Reads from integrated systems (cloud providers, GitHub, Azure DevOps) to answer.
4. Uses an LLM — local (Ollama) or hosted (Azure OpenAI / OpenAI) — to compose answers when retrieval is involved.
5. **Never writes.** All integrations are read-only by contract.

## What Scout is not

- Not a ticketing bot. It does not create issues, send emails, or notify people.
- Not an incident-response tool. It does not page, escalate, or trigger runbooks.
- Not a code-generation bot. It answers questions about code, it does not modify it.
- Not a LLM wrapper. The LLM is an optional composition layer; several plugins work without one.

## Target users

- **Business users** who need to ask questions about the product or codebase without context-switching to developer tooling.
- **On-call engineers** who want a faster surface for status checks than logging into cloud portals.
- **New team members** who need to self-serve onboarding questions about the codebase.

## Success criteria for V1

- A team can install Scout on Discord.
- Both reference plugins ship and work against real integrations:
  - `CloudStatus` returns current health for Azure, AWS, Oracle Cloud,
    and Kubernetes clusters.
  - `CodeQA` answers natural-language questions about a configured GitHub
    repository, with citations to source files.
- The LLM provider is swappable between Ollama and a hosted option (Azure
  OpenAI or OpenAI) via configuration only, no code changes.
- All external calls are demonstrably read-only (audited in code review).
- The plugin system proves its extensibility by hosting two domains that
  share no code beyond the `KernelPlugin` contract.

## Non-goals for V1

- Slack and Teams adapters (planned for V1.1 and V1.2 — see
  [[pages/decisions/0001-platform-priority|ADR-001]]).
- Multi-repo indexing in `CodeQA` (V1 supports one configured GitHub repo).
- Azure DevOps integration in `CodeQA` (GitHub only for V1).
- Multi-tenant hosting.
- Web UI or admin dashboard. When a web surface is eventually built
  post-V1, the stack is **Angular** (latest stable, Signals-first,
  standalone components) — no alternatives considered. See
  [[pages/decisions/0004-web-stack-angular|ADR — Web stack is Angular]].
