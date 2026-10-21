---
title: "Electron Provisioning UI - feasibility study: Problem statement"
version: "1.0"
date: 2026-04-22
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: longform
status: active
description: "Problem statement: Scout's current manual bot install story for contributors and operators, and the goals of a provisioning UI."
source:
  - "obsidian-vault/scout/02-architecture/electron-provisioning-ui.md"
reliability: medium
---

# Electron Provisioning UI - feasibility study: Problem statement

Scout V1 targets Discord (see [[pages/decisions/0001-platform-priority|ADR-001]]).
Its current install story for a contributor or operator is:

1. Create a Discord application in the Developer Portal.
2. Enable the bot, copy the token, configure intents.
3. Build an OAuth2 URL with the right scopes, open it, install to a guild.
4. Drop the token into env vars / user secrets.
5. `dotnet run --project Scout.Host.Worker`.

For an OSS chatops bot that wants wide adoption, five manual steps is high
friction — especially when a user wants to "just try it" on Discord, Slack
*and* Teams side by side.

The thesis: an Electron desktop app that detects installed clients and
runs per-platform provisioning wizards could collapse this to "click
Discord → sign in → done", launching `Scout.Host.Worker` locally with the
token already wired.

This note tests that thesis against each platform's policy and API
surface.
