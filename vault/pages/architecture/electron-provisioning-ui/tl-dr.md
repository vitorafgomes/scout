---
title: "Electron Provisioning UI - feasibility study: TL;DR"
version: "1.0"
date: 2026-04-22
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: longform
status: active
description: "Summary of the Electron provisioning feasibility study and its per-platform verdict."
source:
  - "obsidian-vault/scout/02-architecture/electron-provisioning-ui.md"
reliability: medium
---

# Electron Provisioning UI - feasibility study: TL;DR

- **Feasible as a wizard, not as one-click.** Detection of installed
  desktop clients is straightforward across OSes. End-to-end *automated* bot
  provisioning is only possible on **Slack** (via the App Manifest API).
  Discord and Teams require irreducible human steps.
- **Discord is blocked on the Developer Portal.** There is no public API to
  create an Application or a Bot. All we can do is deep-link the user into
  the portal, then automate the OAuth2 install into the guild and token
  capture.
- **Slack is the best story.** `apps.manifest.create` + OAuth install can be
  fully automated *provided the user obtains an app configuration token
  first* (which is itself a manual, 2-minute web step). Socket Mode removes
  the need for a public HTTPS endpoint.
- **Teams is effectively impossible for V1.** Sideloading a custom Teams app
  requires tenant-admin policy (`Upload custom apps = On`). Without that,
  nothing we build can bypass it.
- **Recommendation: defer Electron UI to post-V1.** Ship Scout V1 as a .NET
  worker with a `scout init` CLI wizard. Invest in Electron provisioning
  only after V1 lands, and only if the OSS adoption signal justifies it.
- **Stack: Angular (latest stable, Signals, standalone) + Bun (toolchain)
  + Electron (desktop shell).** No React/Vue/Svelte, no npm/yarn/pnpm, no
  alternative desktop framework. See
  [[pages/decisions/0004-web-stack-angular|ADR — Web stack is Angular]].
- **Renderer stack is Angular (latest stable, Signals-first, standalone
  components).** The Electron renderer is an Angular application built
  with the Angular CLI and bootstrapped inside the Chromium
  `BrowserWindow` that the Electron main process creates.
- **Toolchain is Bun (install, build, test, scripts).** Bun is the
  developer toolchain for the desktop UI; the Electron runtime itself
  owns the main-process lifecycle inside the packaged app.
