---
title: "Electron Provisioning UI - feasibility study: Hard limits / deal-breakers"
version: "1.0"
date: 2026-04-22
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: longform
status: active
description: "Hard limits and deal-breakers found in the Electron provisioning feasibility study."
source:
  - "obsidian-vault/scout/02-architecture/electron-provisioning-ui.md"
reliability: medium
---

# Electron Provisioning UI - feasibility study: Hard limits / deal-breakers

- **Discord application/bot creation cannot be automated.** No public
  API, no OAuth scope, and
  [Discord ToS](https://discord.com/terms) forbids scraping or
  automating the Developer Portal.
- **Discord Message Content Intent** is a manual, human-reviewed toggle
  for apps that grow beyond 100 guilds. Scout's intent request cannot
  be pre-approved by an Electron wizard.
- **Slack config token is user-generated.** The user has to click
  "Generate Token" on api.slack.com once. There is no OAuth flow that
  mints a config token. This is a hard one-click blocker.
- **Slack distribution review** is required to put the app into more
  than the creator's workspace without per-workspace manifest
  re-creation. For personal/OSS use Scout can stay "undistributed", but
  that means every end user creates their own Slack app.
- **Teams sideloading requires tenant-admin policy.** The
  `Upload custom apps` setting in Teams admin center
  ([docs](https://learn.microsoft.com/en-us/microsoftteams/teams-custom-app-policies-and-settings))
  is off-by-default in many enterprises. Nothing Scout does client-side
  bypasses this.
- **Teams needs a public HTTPS endpoint.** Bot Framework does not
  support anything equivalent to Slack Socket Mode on the Teams
  channel. Running "entirely local" requires a tunnel (ngrok, Dev
  Tunnels, Cloudflare Tunnel) baked in, which is an extra moving part
  and a separate ToS surface.
- **Teams Linux desktop does not exist.** Detection on Linux is moot;
  users will be on the web client.
- **.NET binaries cannot be spawned from inside ASAR.** Must be shipped
  as an unpacked resource (`extraResource` in electron-builder /
  `extraResources` in Forge). Minor build config, but non-obvious.
- **Linux `safeStorage` falls back to plaintext-equivalent** when no
  desktop keyring is present. This matters for headless / server /
  Docker-based "Electron via X11 forward" situations — unlikely, but
  worth documenting.
