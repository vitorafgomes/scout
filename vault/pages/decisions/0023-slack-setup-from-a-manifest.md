---
title: "ADR 0023 - Slack is set up from a prefilled app manifest and two pasted tokens"
version: "1.0"
date: 2026-10-28
updated: 2026-11-05
changes: "Merge of the channel operator phase 8c-A and the live assistant: references re-pointed by content. Initial ADR, accepted"
page_type: adr
status: accepted
description: "The desktop opens Slack's app creation flow prefilled with Scout's Socket Mode manifest; the owner pastes the bot token and the app-level token, main checks both with Slack and keeps them in main-only vault keys, and the worker gets them as the two keys SlackOptions binds."
supersedes: ""
superseded_by: ""
source:
  - chat
  - docs.slack.dev (app manifests, Socket Mode, apps.connections.open; read 2026-10-28)
  - ui/packages/main/src/slack/slack-setup.ts
  - ui/packages/main/src/ipc/slack.ts
  - ui/packages/main/src/worker/build-env.ts
  - src/Scout.Adapter.Slack/
reliability: high
---

# ADR 0023 - Slack is set up from a prefilled app manifest and two pasted tokens

> "Create the Slack app" opens Slack with Scout's manifest filled in; the owner installs the app, pastes the bot token and an app-level token, and Scout checks both with Slack before it keeps them.

## Status

- [ ] Proposed
- [x] Accepted
- [ ] Deprecated / superseded

Updated: 2026-10-28.

## Context

The worker already runs Slack over Socket Mode when `Scout:Adapters:Slack:BotToken` and `Scout:Adapters:Slack:AppLevelToken` are both set (`src/Scout.Adapter.Slack/SlackServiceCollectionExtensions.cs:32`, [[pages/services/adapter-slack]]), but the desktop could not configure it: the welcome screen showed Slack as "Coming soon" and `buildEnv` refused to start the worker without a Discord token. The owner asked for Slack in the desktop ([[pages/decisions/0001-platform-priority]] puts Slack after Discord).

What Slack offers, per docs.slack.dev on 2026-10-28:

- `https://api.slack.com/apps?new_app=1&manifest_json=<URL-encoded JSON>` takes the owner straight into the app creation flow with the manifest filled in.
- Socket Mode needs an app-level token (`xapp-`). The docs describe only one way to get one: Basic Information, App-Level Tokens, Generate, with the scope `connections:write`. No API creates it.
- The bot token (`xoxb-`) appears after Install to Workspace.

## Decision

- **Manifest, not a hand-written checklist.** `SLACK_MANIFEST` (`ui/packages/main/src/slack/slack-setup.ts:19`) names the app "Scout" with bot user "scout", turns the Messages tab on and not read-only, turns Socket Mode on, turns interactivity, org deploy and token rotation off, and declares no request URL, so Slack never has to reach the owner's computer. The scopes and events are exactly what the adapter uses:
  - bot events `message.channels`, `message.groups`, `message.im`: the adapter registers one handler, for `MessageEvent` (`src/Scout.Adapter.Slack/SlackChatAdapter.cs:66`), and the translator answers a channel message only when it mentions the bot and a DM always (`src/Scout.Adapter.Slack/SlackMessageTranslator.cs:68`); `app_mention` would never be read.
  - bot scopes `channels:history`, `groups:history`, `im:history` for those three events, and `chat:write` for the reply the sink posts and then updates (`src/Scout.Adapter.Slack/Streaming/SlackResponseSink.cs:84`, `:96`). `auth.test`, which the adapter calls at start, needs no scope.
  - not included: group DMs (`message.mpim`, `mpim:history`). The translator would need a mention there too; add both when an owner asks.
- **Two pasted tokens.** The renderer shows Step 1, "Create the Slack app" (through the existing `scout:app.openExternal` allow-list, which now includes `api.slack.com`, `ui/packages/main/src/ipc/app.ts:11`) with the lines: pick the workspace and Create; Install to Workspace and copy the Bot User OAuth Token; Basic Information, App-Level Tokens, Generate with `connections:write`, copy; invite @scout to each channel. Step 2 is two password fields and Save.
- **Main checks both before it stores either.** `scout:slack.save` (`ui/packages/main/src/ipc/slack.ts:83`) refuses a bot token without `xoxb-` or an app-level token without `xapp-` before any request, then calls `auth.test` with the bot token (team and bot user name come back) and `apps.connections.open` with the app-level token, each a `POST` with the token in the `Authorization` header only and a 5 s timeout (`ui/packages/main/src/slack/slack-setup.ts:58`, `:74`). Slack answers a bad token with HTTP 200 and `{ok:false}`, so the body decides: `invalid_auth`, `not_authed`, `token_revoked`, `token_expired`, `account_inactive` mean "Slack rejected the bot token." or "... the app-level token."; any other Slack error or a non-JSON answer is "Slack could not check the <token> (<error or HTTP status>)."; a failed request is "Could not reach Slack. Check the connection and try again." Only then are both tokens stored, in one vault write, and a running worker restarts.
- **Custody.** Both tokens and the team and bot user names `auth.test` returned live in one main-only vault entry, `slack.connection` (`ui/packages/main/src/ipc/slack.ts:21`), written with a single store (`:91`), so a failed write leaves the previous pair whole and the worker never gets a new bot token beside an old app-level token. `scout:slack.status` answers `{connected, team, botUser}` from it without a call to Slack. The renderer never receives a token back, and no log line, IPC answer or error message carries one ([[pages/invariants/secrets-never-in-source]]). `scout:slack.disconnect` deletes the entry and restarts a running worker.
- **Worker environment.** `buildEnv` sets `SCOUT__ADAPTERS__SLACK__BOTTOKEN` and `SCOUT__ADAPTERS__SLACK__APPLEVELTOKEN` when both tokens exist (`ui/packages/main/src/worker/build-env.ts:73`), the keys `SlackOptions` binds. It now needs Discord or Slack and refuses to start only when neither is connected, so a Slack-only owner runs Scout. Since [[pages/decisions/0028-local-quick-ask-assistant]] it no longer refuses: with neither, the worker runs with only the local adapter.
- **Not an owner credential.** Slack tokens let Scout read and reply in the owner's workspace; they do not reach the owner's repositories, clusters or plan, so they do not set `SCOUT__ACCESS__REQUIREALLOWLIST` ([[pages/decisions/0021-allowed-users]]).
- **Classification.** `auth.test` and `apps.connections.open` are a read and a handshake: the first returns who the token belongs to, the second returns a one-time Socket Mode URL that main drops without opening. Neither changes anything in the workspace. Creating and installing the app is something the owner does in Slack's own pages; Scout only opens the link. The worker's writes stay the reply post and its updates in the originating channel ([[pages/decisions/0009-descriptive-vs-executive-writes]], [[pages/invariants/no-executive-writes]]).

## Consequences

- **Better:** the owner sets up Slack without editing scopes or events by hand, the manifest cannot ask for more than the adapter uses, and a wrong or swapped token is caught before it is stored. A Discord owner adds Slack from Settings; a new owner can start with Slack alone from the welcome screen.
- **Worse:** two tokens to copy by hand, because the app-level token has no API. In a channel Scout answers only after someone invites @scout there.
- **Neutral but noteworthy:** a token revoked in Slack after Save still reads as Connected until the worker fails to start; status does not call Slack.

## Alternatives considered

- **OAuth install with a redirect to a loopback server**, as Discord does. Socket Mode still needs the hand-made app-level token, and a distributed Slack app needs a public redirect URL and a client secret Scout would have to ship.
- **The App Manifest API (`apps.manifest.create`)**. It needs a configuration token the owner would also generate by hand, and it creates the app with one more credential in Scout's custody; the prefilled link does the same with none.
- **`app_mention` instead of `message.channels`.** The adapter reads `MessageEvent` only; supporting `app_mention` is an adapter change, not a setup one.

## Open question

[[pages/decisions/0021-allowed-users]] deferred qualifying allowed ids as `Adapter:UserId` "when a second adapter ships in the desktop app". This is that event. Ids stay unqualified here; a Discord snowflake (digits), a Slack member id (`U...`) and a Teams `29:...` id do not collide in practice, and the owner decides whether to qualify them.

## Enforcement

- `ui/packages/main/test/slack-setup.test.ts:45` (the manifest link decodes to exactly the expected JSON), `:71` to `:116` (auth.test success, rejection, network failure, 502, wrong prefix), `:128` to `:169` (apps.connections.open success with the URL dropped, rejection, `missing_scope`, network failure, wrong prefix).
- `ui/packages/main/test/ipc-slack.test.ts:107` (save stores both in one entry and restarts), `:132` (no token or socket URL in a log line or an answer), `:148` (a rejected app-level token stores nothing and restarts nothing), `:218` (a vault that fails on a re-save keeps the previous connection, gives `vault/unavailable` and restarts nothing), `:235` (a vault that stops after 0, 1 or 2 writes never leaves a mixed pair), `:257` (disconnect clears both).
- `ui/packages/main/test/worker-build-env.test.ts:47`, `:69`, `:650` (Slack only; Slack absent; Slack does not require the allow list). Amended 2026-10-30: with neither platform the worker no longer refuses to start; it runs only the local adapter (`ui/packages/main/test/worker-build-env.test.ts:40`, [[pages/decisions/0028-local-quick-ask-assistant]]).
- Invariants: [[pages/invariants/secrets-never-in-source]], [[pages/invariants/no-executive-writes]], [[pages/invariants/pluggable-boundaries]].

## Related

- Services affected: [[pages/services/desktop-ui]], [[pages/services/adapter-slack]]
- Related decisions: [[pages/decisions/0001-platform-priority]], [[pages/decisions/0009-descriptive-vs-executive-writes]], [[pages/decisions/0021-allowed-users]]
