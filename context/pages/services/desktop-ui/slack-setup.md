---
title: "Desktop UI: Slack setup"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Desktop UI service page."
page_type: service
status: active
description: "Read when a change touches: Slack setup."
source: - ui/packages/main/src - ui/packages/preload/src - ui/packages/renderer/src - ui/packages/shared/src - ui/build
reliability: high
---

## Slack setup

The Chat app step's Slack card ("Not connected", or "Connected" once Slack is connected), chosen and continued, opens the `slack` route, and the Slack card under Chat clients in Settings expands the same `sc-slack-setup` component (`ui/packages/renderer/src/app/wizard/slack/slack-setup.component.ts:139`), so an owner who started with Discord adds Slack later ([[pages/decisions/0023-slack-setup-from-a-manifest]]). It calls four channels, `scout:slack.manifestUrl`, `save`, `status` and `disconnect`, handled in `ui/packages/main/src/ipc/slack.ts:58`. What the owner sees:

- **Step 1, Create the Slack app:** the button opens the link from `manifestUrl()` (`ui/packages/main/src/slack/slack-setup.ts:38`) through `scout:app.openExternal`, whose allow-list now includes `api.slack.com`, then four lines: pick the workspace and click Create; Install to Workspace and copy the Bot User OAuth Token; Basic Information, App-Level Tokens, Generate Token and Scopes with `connections:write`, copy the token; invite @scout to each channel where it should answer.
- **Step 2:** two password fields, "Bot User OAuth Token (xoxb-)" and "App-level token (xapp-)", and Save, disabled until both are filled. A refusal shows main's reason, for example "The bot token starts with xoxb-." or "Slack rejected the app-level token.", and keeps the form.
- **Connected:** "Connected to <team> as <botUser>" and Disconnect.

Main refuses a token without its prefix before any request, checks the bot token with `auth.test` and the app-level token with `apps.connections.open` (`ui/packages/main/src/slack/slack-setup.ts:58`, `:74`), and only then stores both tokens with the `auth.test` team and bot user as one entry, `slack.connection`, in a single vault write (so a failed write leaves the previous pair whole), and restarts a running worker through `restartWorkerIfRunning`. When no worker has started yet in this session, the renderer starts it after Save (`ui/packages/renderer/src/app/wizard/slack/slack-setup.component.ts:182`), so a Slack-only owner reaches a running Scout from the welcome screen. `readSlackTokens` (`ui/packages/main/src/ipc/slack.ts:34`) feeds `buildEnv`, which sets `SCOUT__ADAPTERS__SLACK__BOTTOKEN` and `SCOUT__ADAPTERS__SLACK__APPLEVELTOKEN` when both exist (`ui/packages/main/src/worker/build-env.ts:80`) and, since [[pages/decisions/0028-local-quick-ask-assistant]], starts even when neither is connected, with only the local adapter. Slack does not set `SCOUT__ACCESS__REQUIREALLOWLIST`.

`WizardStore` keeps the two platforms apart (`ui/packages/renderer/src/app/core/wizard.store.ts:81`, `:91`): `hasDiscord` drives the Discord card, and `hasProvisioning`, Discord, Slack or the local-only choice (see Quick ask), decides the post-connect shell (on Launch only once the worker runs) and the welcome screen's worker start. Disconnect on the Discord card in Settings forgets only the Discord app (see Settings).
