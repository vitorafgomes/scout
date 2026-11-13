---
title: "Adapter.Teams"
version: "1.5"
date: 2026-10-21
updated: 2026-11-11
changes: "A sink for a group chat or channel, where streaming is off, declares no fine-grained editing, so the pump sends it no interim render and writes only the final (2026-11-11).."
page_type: service
status: active
description: "Microsoft Teams adapter: the Bot Framework turn handler, translator and streaming sink; the inbound webhook is deliberately not wired in this repository."
source: ["src/Scout.Adapter.Teams/", "tests/Scout.Adapter.Teams.Tests/"]
reliability: high
---

# Adapter.Teams

> The Teams half of Scout that is testable without Azure: it handles a turn once something hands it one.

## Responsibility

Owns turn handling, message translation and the response sink over an abstract activity sender. Does not own the HTTP endpoint, the `CloudAdapter` or the `IBot` that forward Bot Framework requests; the source states these are not registered (`src/Scout.Adapter.Teams/TeamsServiceCollectionExtensions.cs:18`).

## Rules

The rules this project must keep, and what to read before changing it, are in `.claude/rules/adapter-teams.md`. Claude Code loads that file when a task touches the project.

## Topics

Read the page your change touches, not all of them.

| Page | Read when a change touches | Characters |
|---|---|---|
| [[pages/services/adapter-teams/public-contract\|Public contract]] | Public contract | 1503 |
| [[pages/services/adapter-teams/how-it-works\|How it works]] | How it works | 4114 |
| [[pages/services/adapter-teams/hot-spots\|Hot spots]] | Hot spots | 653 |
| [[pages/services/adapter-teams/testing-footprint\|Testing footprint]] | Testing footprint | 3097 |

## Related

Direct project references: [[pages/services/conversation]]. Referenced by [[pages/services/host-console]]. Map: [[pages/architecture/project-map]]. Dated changes: [[pages/history/adapter-teams]].
