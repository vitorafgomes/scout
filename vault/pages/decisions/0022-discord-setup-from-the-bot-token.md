---
title: "ADR 0022 - Discord setup from the bot token"
version: "1.1"
date: 2026-10-23
updated: 2026-10-28
changes: "Invite adds Create Public Threads for the reply thread per conversation"
page_type: adr
status: accepted
description: "The desktop sets up the Discord bot from the pasted bot token alone: it reads the application, turns on Message Content, and adds the bot to a server through a plain bot invite."
supersedes: ""
superseded_by: ""
source:
  - chat
  - docs.discord.com (Application resource, OAuth2 bot authorization, gateway intents; read 2026-10-23)
  - ui/packages/main/src/discord/discord-setup.ts
  - ui/packages/main/src/ipc/discord.ts
  - ui/packages/renderer/src/app/wizard/
reliability: high
---

# ADR 0022 - Discord setup from the bot token

> The owner creates the Discord application and copies its bot token; the desktop does everything else from that token, with no Application ID field, no intent checkboxes and no OAuth loopback.

## Status

- [ ] Proposed
- [x] Accepted
- [ ] Deprecated / superseded by

Updated: 2026-10-23.

## Context

The owner asked for connecting Discord to be easier and, where possible, automatic. The first wizard took the bot token and the Application ID in two fields, asked the owner to tick three intent checkboxes after switching them on in the Developer Portal, then ran an OAuth code flow through a loopback server on `127.0.0.1:53682` to learn the server the bot was added to.

Discord has no API to create an application or a bot, so the owner still has to create the application in the Developer Portal and copy the bot token. Everything after that can be done with the bot token alone (docs.discord.com, read 2026-10-23):

- `GET /applications/@me` with `Authorization: Bot <token>` returns the application behind the token: id, name, the bot user, `flags` and `approximate_guild_count`.
- `PATCH /applications/@me` with `{flags}` may change only the limited intent flags (`GATEWAY_PRESENCE_LIMITED`, `GATEWAY_GUILD_MEMBERS_LIMITED`, `GATEWAY_MESSAGE_CONTENT_LIMITED`). `GATEWAY_MESSAGE_CONTENT_LIMITED` (`1 << 19`) is the switch for applications in under 100 servers; `GATEWAY_MESSAGE_CONTENT` (`1 << 18`) is the one granted to verified applications in 100 or more.
- A bot invite of the form `https://discord.com/oauth2/authorize?client_id=<id>&scope=bot&permissions=<int>` needs no `redirect_uri`, no code exchange and no client secret. Adding scopes beyond `bot` and `applications.commands` turns it into a code flow.
- `GET /users/@me/guilds` with the bot token lists the servers the bot is in. This endpoint was not re-read on the docs in this session; the tests use a fake for it.

The Discord adapter needs the Message Content intent: it requests `GatewayIntents.MessageContent` (`src/Scout.Adapter.Discord/DiscordChatAdapter.cs:69`). Without the application flag, Discord is expected to refuse the gateway connection with a disallowed-intents close. That claim was not re-read on the docs in this session.

## Decision

The wizard has three steps.

1. **Create your Discord bot.** A button opens the Developer Portal, with three short lines: New Application, name it Scout; Bot, Reset Token; Copy.
2. **Paste the bot token.** One password field. On paste or Save, the renderer sends the token to the main process once (`scout:discord.checkToken`). Main reads the application, stores the token as `discord.bot-token` and the application id as `discord.client-id`, and returns the application and bot names. The renderer then asks main to turn Message Content on (`scout:discord.enableIntent`). Main reads the flags again and does one of three things:
   - When either message-content bit is set, it does nothing.
   - When the application is in under 100 servers, it sends a PATCH with the old flags plus `GATEWAY_MESSAGE_CONTENT_LIMITED`.
   - When the application is in 100 or more servers, it returns the Developer Portal bot page and a message instead of a PATCH. A refused PATCH returns the same kind of answer.

   Continue stays off until the intent is on. "Check again" retries.
3. **Add Scout to your server.** Main builds the invite (`scout:discord.inviteUrl`) with `scope=bot` and six permissions (integer `309237730304`, `ui/packages/main/src/discord/discord-setup.ts:27`):

- View Channel, Send Messages, Send Messages in Threads and Embed Links cover what the adapter does: it reads a mention, posts an embed and edits it.
- Create Public Threads (`1 << 35`) lets the Discord adapter open one thread per conversation and reply inside it, so a conversation does not interleave with the channel. Added on 2026-10-28 for the Discord chat branch. A server where Scout was invited before then has to grant it again, by re-inviting the bot or giving its role the permission.
- Read Message History is kept as the plan asked, although the adapter makes no history call and may not need it.

The permission bit positions were not re-read on the docs in this session. The renderer opens it through the existing `scout:app.openExternal` allow-list, which already holds `discord.com`. When the step opens it lists the servers the bot is already in (`scout:discord.guilds`), and the owner can pick one. After the invite it polls that list every 3 s, at most 100 times, with `setTimeout`, and stops when the step is left. It continues on its own when a server that was not in the first list appears, storing `discord.guild-id` and `discord.guild-name`.

All Discord HTTP runs in the Electron main process (`ui/packages/main/src/discord/discord-setup.ts`). The renderer never makes a Discord call and never gets the token back. The vault keys keep their names, so `ui/packages/main/src/worker/build-env.ts` and the worker are unchanged. The worker still reads only the bot token.

## Classification of the PATCH

ADR 0009 forbids executive writes on the owner's systems. The PATCH changes one setting of Scout's own Discord application, the bot Scout runs as. It is Scout's own configuration, in the same class as the `features.json` and `local-runtimes.json` files the invariant already lists as Scout's own state.

- **Who starts it:** only the owner, from the desktop wizard, by pasting a token or pressing "Check again".
- **What it can change:** only the limited message-content flag. Discord's PATCH can change only the limited intent flags, and the request sends the existing flags back unchanged apart from that one bit.
- **What it cannot reach:** the owner's servers, channels and members. It is not an LLM-invocable function, plugin operation or worker code path. The model cannot reach it, because the call lives in the Electron main process and the worker has no route to it.

It is therefore not an executive write in the sense of [[pages/invariants/no-executive-writes]].

## Consequences

- **Better:**
  - The owner types one value instead of two.
  - The owner ticks no checkboxes.
  - The browser round trip through a local port and the client secret question are both gone.
  - A wrong token is caught at paste time ("Discord rejected this bot token.").
  - A missing intent is caught before the worker starts.
- **Worse:**
  - The owner still creates the application and copies the token by hand; Discord offers no way around that.
  - Above 100 servers the intent needs a manual step in the portal, which the wizard explains with a deep link.
- **Neutral but noteworthy:**
  - Invite permissions no longer include Use Application Commands; Scout registers no slash commands.
  - `discord.client-id`, `discord.guild-id` and `discord.guild-name` remain metadata for the desktop. The worker does not read them.
  - The OAuth loopback server stays because the ChatGPT sign-in uses it. `DiscordTokenExchangeError` also stays, for the same reason.

## Alternatives considered

- **Keep the OAuth code flow to learn the server.** Rejected. It needs a redirect URI registered in the portal, and possibly a client secret, which is one more thing for the owner to copy. Polling the bot's server list gives the same answer with the token Scout already has.
- **Create the application by automation.** Not possible: Discord has no API for it, and driving the portal through a browser is fragile and against its terms.
- **Ask the owner to switch the intent on in the portal.** Rejected below 100 servers, because the API does it. It stays as the fallback above that.

## Enforcement

- `ui/packages/main/test/discord-setup.test.ts` covers the PATCH: only the limited bit is added, the other bits are kept, an intent that is already on means no PATCH, and 100 or more servers means a portal link with no PATCH. It also asserts the exact invite URL and permissions, and the 401, network and 403 answers.
- `ui/packages/main/test/ipc-discord.test.ts` asserts that the token appears in no IPC response, error or log line, and that every request goes to `discord.com`.
- Renderer specs `token-paste.component.spec.ts` and `oauth-install.component.spec.ts` cover the one-field step and the automatic server detection.

## Related

- Services affected: [[pages/services/desktop-ui]], [[pages/services/adapter-discord]]
- Invariants: [[pages/invariants/no-executive-writes]], [[pages/invariants/secrets-never-in-source]]
- ADRs: [[pages/decisions/0009-descriptive-vs-executive-writes]], [[pages/decisions/0011-backend-frontend-grpc-uds]]
- Who may talk to the bot, including pairing by code: [[pages/decisions/0021-allowed-users]]
