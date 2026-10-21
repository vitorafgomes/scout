---
title: "Electron Provisioning UI - feasibility study: Prior art"
version: "1.0"
date: 2026-04-22
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: longform
status: active
description: "Prior art for automated app and bot provisioning (app manifests, OAuth install flows) relevant to the Electron provisioning UI."
source:
  - "obsidian-vault/scout/02-architecture/electron-provisioning-ui.md"
reliability: medium
---

# Electron Provisioning UI - feasibility study: Prior art

| Project | URL | What it solved | What it didn't |
|---|---|---|---|
| **Probot / GitHub App Manifest** | [registering-a-github-app-from-a-manifest](https://docs.github.com/en/apps/sharing-github-apps/registering-a-github-app-from-a-manifest) | Full programmatic GitHub App creation from a manifest posted to a URL; GitHub redirects with a temp code that exchanges for the app's private key, webhook secret, client secret, app id. The closest real-world analog to what Scout wants, and it *does* work end-to-end. | GitHub-only. Discord has nothing equivalent. |
| **Slack App Manifests** | [apps.manifest.create](https://docs.slack.dev/reference/methods/apps.manifest.create/) | Programmatic Slack app creation; matches the Slack section above. | Requires the one-time config token step; the rest is clean. |
| **Botpress (OSS)** | [github.com/botpress/v12](https://github.com/botpress/v12) | Self-hosted chatbot builder with per-channel connectors; the user creates the bot registration on each platform manually, then pastes credentials into Botpress. | No attempt at automating the platform-side registration. Botpress punts to the same wizard-with-deep-links UX Scout would ship. |
| **GitHub Desktop "Install a GitHub App"** | — | Smooth deep-link flow into GitHub for install consent, handles the OAuth dance. | Only works because the app already exists on GitHub's side; equivalent for Scout per user's chat platform is impossible on Discord. |
| **Microsoft 365 Agents Toolkit** | [VS Code extension + CLI](https://learn.microsoft.com/en-us/microsoftteams/platform/toolkit/publish) | Automates the Teams manifest + Azure app registration + sideload flow from a developer's machine. Worth reading as the reference for "how far can this be pushed for Teams". | Still requires an Azure subscription and tenant-admin sideload policy. Packaged as a dev tool, not an end-user installer. |
| **Discord `Integration.requires_code_grant`** | [OAuth2 docs](https://discord.com/developers/docs/topics/oauth2) | Lets an installer force the install to go through a real OAuth authorization code grant, giving Scout a clean token exchange moment. | Does not create the app itself. |

**No OSS Electron shell currently wraps "bot install flows" across Discord/Slack/Teams.** This is green-field.
`TODO: verify` by searching GitHub topics `electron bot installer`, `chatops desktop` — preliminary search was negative.
