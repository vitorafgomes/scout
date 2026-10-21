---
title: "ADR-001 - Chat platform rollout order"
version: "1.0"
date: 2026-04-21
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: adr
status: accepted
description: "Decision on the rollout order of chat platforms: Discord first, then Slack and Teams, to limit the surface area of the first release."
source:
  - "obsidian-vault/scout/06-decisions/2026-04-21-platform-priority.md"
reliability: medium
---

# ADR-001 - Chat platform rollout order

## Status

Accepted — 2026-04-21

## Context

Scout must eventually support Slack, Discord, and Microsoft Teams. Supporting all three at once would triple the surface area of the first release (three SDKs, three auth models, three message-rendering layers) without improving core value.

## Decision

Implement chat adapters sequentially, in order of **integration friction**:

1. **Discord** — first.
   - Bot token is a single string, created by clicking through the Developer Portal.
   - `Discord.Net` is a mature, well-documented .NET SDK.
   - No app review, no workspace admin, no tenant consent.
   - Can be tested on a personal server in minutes.
2. **Slack** — second.
   - Requires a Slack App, OAuth scopes, and either Socket Mode (dev) or a public HTTPS endpoint (prod).
   - SDK option: `SlackNet` (community, actively maintained).
   - Distribution outside a single workspace requires review.
3. **Microsoft Teams** — third.
   - Requires `Microsoft.Bot.Framework` plus an Azure Bot Service registration.
   - Requires a Teams app manifest and admin consent inside the target tenant.
   - Most ceremony to stand up even a "hello world" bot.

## Consequences

- The first release targets Discord only. The `IChatAdapter` abstraction must still be in place so Slack and Teams slot in without changing `Scout.Core`.
- Documentation and onboarding examples for V1 assume Discord. A second onboarding guide is added when Slack ships.
- Any chat feature that would only make sense on one platform (e.g., Block Kit-specific interactions) goes behind a capability flag on the adapter, not in Core.

## Alternatives considered

- **Teams first** — rejected. Highest integration cost, narrowest contributor base, tied to Microsoft 365 licensing.
- **All three in parallel** — rejected. Forces the adapter abstraction to stabilize before Core has shipped, and triples V1 scope.
- **Slack first** — rejected. Slack is a plausible second choice, but Discord has zero setup friction for contributors, which matters for an OSS project.
