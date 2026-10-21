---
title: "Electron Provisioning UI - feasibility study"
version: "1.0"
date: 2026-04-22
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: longform
status: active
description: "Hub for the feasibility study of an Electron provisioning UI that lets users set up chat platform bots without manual developer-portal steps, with per-platform limits and a recommendation."
source:
  - "obsidian-vault/scout/02-architecture/electron-provisioning-ui.md"
reliability: medium
---

# Electron Provisioning UI - feasibility study

Implementation landed in Plan 5 / M1; see the plan and ADR for scope + deferrals.

> Can we ship an Electron desktop app that detects installed chat clients,
> and then walks the user through provisioning a Scout bot for each platform
> with as little manual work as possible?
>
> Short answer: **partially yes, fully no — and the line cuts differently per
> platform.** See TL;DR.

## Parts

- [[pages/architecture/electron-provisioning-ui/tl-dr|TL;DR]]
- [[pages/architecture/electron-provisioning-ui/problem-statement|Problem statement]]
- [[pages/architecture/electron-provisioning-ui/per-platform-feasibility|Per-platform feasibility]]
- [[pages/architecture/electron-provisioning-ui/desktop-app-detection-across-oses|Desktop-app detection across OSes]]
- [[pages/architecture/electron-provisioning-ui/electron-architecture-sketch|Electron architecture sketch]]
- [[pages/architecture/electron-provisioning-ui/prior-art|Prior art]]
- [[pages/architecture/electron-provisioning-ui/hard-limits-deal-breakers|Hard limits / deal-breakers]]
- [[pages/architecture/electron-provisioning-ui/recommendation|Recommendation]]
- [[pages/architecture/electron-provisioning-ui/open-questions|Open questions]]
- [[pages/architecture/electron-provisioning-ui/references|References]]
