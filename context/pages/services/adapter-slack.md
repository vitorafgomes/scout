---
title: "Adapter.Slack"
version: "1.8"
date: 2026-10-21
updated: 2026-11-11
changes: "A start whose auth.test or Socket Mode connect fails stops the adapter before it throws, and a stop that lands during the start wins: the start checks the lifecycle under a connect gate the stop also takes, so the socket is never left open after a stop, and the connect runs on the lifetime token."
page_type: service
status: active
description: "Slack chat adapter over Socket Mode: filters bot traffic, streams the reply by posting then updating one message."
source: ["src/Scout.Adapter.Slack/", "tests/Scout.Adapter.Slack.Tests/"]
reliability: high
---

# Adapter.Slack

> Connects Scout to Slack without a public endpoint, using a bot token and an app-level token.

## Responsibility

Owns the Socket Mode connection, message filtering and the response sink. Does not own routing or memory ([[pages/services/conversation]]).

## Rules

The rules this project must keep, and what to read before changing it, are in `.claude/rules/adapter-slack.md`. Claude Code loads that file when a task touches the project.

## Topics

Read the page your change touches, not all of them.

| Page | Read when a change touches | Characters |
|---|---|---|
| [[pages/services/adapter-slack/public-contract\|Public contract]] | Public contract | 2027 |
| [[pages/services/adapter-slack/how-it-works\|How it works]] | How it works | 4820 |
| [[pages/services/adapter-slack/desktop-setup\|Desktop setup]] | Desktop setup | 1017 |
| [[pages/services/adapter-slack/hot-spots\|Hot spots]] | Hot spots | 559 |
| [[pages/services/adapter-slack/testing-footprint\|Testing footprint]] | Testing footprint | 2926 |

## Related

Direct project references: [[pages/services/conversation]]. Referenced by [[pages/services/host-console]]. Map: [[pages/architecture/project-map]]. Dated changes: [[pages/history/adapter-slack]].
