---
title: "ADR 0021 - Only allowed users can use Scout once an owner credential is connected"
version: "1.2"
date: 2026-10-23
updated: 2026-11-04
changes: "Amendment 2026-11-04: Scout:Access:OwnerUserIds names the owner among the allowed users, for the operator's owner-only functions from chat; Amendment: the owner pairs from chat with a one-time code sent in a direct message, bare or as pair <code>; the code burns on exposure or after five wrong codes; the empty-list and pairing refusals say how to pair"
page_type: adr
status: accepted
description: "The conversation core answers only the platform user ids the owner lists in the desktop app; with an empty list it refuses everyone once GitHub, Kubernetes or the ChatGPT plan is connected, and answers everyone otherwise. The owner can add a user by sending a one-time code to the bot in a direct message."
supersedes: ""
superseded_by: ""
source:
  - chat
  - src/Scout.Conversation/ConversationConductor.cs
  - src/Scout.Conversation/Access/AccessOptions.cs
  - src/Scout.Host.Console/StdoutPairingSignal.cs
  - ui/packages/main/src/ipc/access.ts
  - ui/packages/main/src/worker/build-env.ts
reliability: high
---

# ADR 0021 - Only allowed users can use Scout once an owner credential is connected

> Scout answers only the chat users the owner lists under Settings, "Who can use Scout"; while the list is empty it refuses everyone if a GitHub, Kubernetes or ChatGPT plan connection is in place, and answers everyone if none is.

## Status

- [ ] Proposed
- [x] Accepted
- [ ] Deprecated / superseded

Updated: 2026-11-04 (amendment: the owner from chat; 2026-10-28: pairing by code).

## Context

A security review on 2026-10-23 found that anyone who can message the bot drives the owner's credentials: the GitHub token ([[pages/decisions/0019-github-app-device-flow]]), the Kubernetes contexts the owner ticked, including `-admin` contexts of production clusters ([[pages/decisions/0020-kubernetes-from-local-kubeconfig]]), and the owner's ChatGPT plan ([[pages/decisions/0018-chatgpt-plan-sign-in]]). The plugins only read, but what they read (private repositories, cluster state) is disclosed to whoever asks, and plan usage counts against the owner's limits. The owner approved restricting who can use Scout before the desktop-connections PR. [[pages/invariants/local-first-default]] requires that a contributor with local Ollama and no credentials keeps working with zero configuration.

## Decision

- **Gate in the conversation core.** `ConversationConductor.HandleAsync` checks `ChatMessage.UserId` against `Scout:Access` before it logs the routing line, routes, pumps or calls the model (`src/Scout.Conversation/ConversationConductor.cs:137`, rule at `:342`). `AccessOptions` holds `AllowedUserIds` and `RequireAllowList` (`src/Scout.Conversation/Access/AccessOptions.cs:8`), bound in `AddScoutConversation` (`src/Scout.Conversation/ConversationServiceCollectionExtensions.cs:34`). Ids are compared ordinally after trimming; blank entries are ignored.
  - A non-empty list without the sender: the reply is exactly "Scout only answers its configured users."
  - An empty list with `RequireAllowList = true`: the reply is exactly "Scout is not set up for anyone yet. If you are the owner, open the Scout desktop app, go to Settings, choose Pair from chat and send the 6-digit code here in a direct message." (reworded 2026-10-30: the owner sent the bot ordinary messages and the old text did not say how to pair)
  - Otherwise nothing changes.
  - A refusal opens the sink and completes it with that text and no citations, then returns; the router, the stream pump, the LLM and every plugin stay untouched. It logs one Information line with the channel id only, never the message text or the sender id.
- **Fail closed only when an owner credential is connected.** The desktop sets `SCOUT__ACCESS__REQUIREALLOWLIST=true` when GitHub is connected, a Kubernetes selection exists, or `llm.provider` is `ChatGptPlan` (`ui/packages/main/src/worker/build-env.ts:80`). With none of these and an empty list the variable is absent, so a fresh clone, a contributor on local Ollama, and a desktop install with only an API-key provider behave as before.
- **The owner manages the list in the app.** Settings has a "Who can use Scout" section with an id field, Add, a list with Remove, and a line on where each platform shows a user id. Main validates every list it receives (`ui/packages/main/src/ipc/access.ts:52`): each id trimmed, 1 to 64 characters from `[A-Za-z0-9._:@-]`, de-duplicated, at most 50; one bad id rejects the whole list. The list is stored under the main-only vault key `access.allowed-users` (not a secret, but the renderer only reaches it through `scout:access.get` and `scout:access.set`), reaches the worker as `SCOUT__ACCESS__ALLOWEDUSERIDS__<n>` (`ui/packages/main/src/worker/build-env.ts:77`), and a running worker restarts when the list changes.
- **Ids are platform user ids, not platform-qualified.** Discord passes the author snowflake (`src/Scout.Adapter.Discord/DiscordMessageTranslator.cs:71`), Slack the member id (`src/Scout.Adapter.Slack/SlackMessageTranslator.cs:81`), Teams `activity.From.Id` (`src/Scout.Adapter.Teams/TeamsMessageTranslator.cs:122`), which is the Bot Framework user id starting with `29:`, not the Microsoft Entra object id. ponytail: an id listed for one platform also admits the same string on another; a collision between a Discord snowflake, a Slack `U...` id and a Teams `29:...` id is theoretical. Qualify ids as `Adapter:UserId` when a second adapter ships in the desktop app.
- **Classification.** The refusal is a descriptive write in the originating channel, through the same sink as any reply ([[pages/decisions/0009-descriptive-vs-executive-writes]]). Identity here decides who receives replies at all; like [[pages/decisions/0014-per-request-identity-gating]], it never authorizes a write. The user id comes from the platform event, so text in a message cannot change it.

## Amendment 2026-10-28: pairing by code

The owner asked for an easier way to connect, without copying a Discord user id by hand. The allow-list stays the only gate; pairing is one more way to fill it.

- **The code.** Settings, "Who can use Scout", has "Pair from chat". Main draws a 6-digit code from `crypto.randomInt`, valid for 10 minutes, keeps it in memory only and returns it to the renderer, which shows "Send `482913` to Scout in a direct message on Discord." (`ui/packages/main/src/ipc/access.ts:114`, `ui/packages/renderer/src/app/wizard/settings/settings.component.ts:485`). While the code is pending and unexpired, `buildEnv` passes it as `SCOUT__ACCESS__PAIRINGCODE` and `SCOUT__ACCESS__PAIRINGEXPIRESAT` (ISO 8601, `ui/packages/main/src/worker/build-env.ts:87`); issuing a code restarts a running worker. A new click replaces the code.
- **The gate.** Before the allow-list, `HandleAsync` treats two shapes of trimmed text as a pairing attempt (`src/Scout.Conversation/ConversationConductor.cs:122`):
  - `pair`, in any case, then one or more spaces and six ASCII digits, anywhere;
  - exactly six ASCII digits, only in a direct message and only while a code is pending: configured, unexpired, a pairing signal registered and not used or burned in this process (`src/Scout.Conversation/ConversationConductor.cs:194`). The bare form was added on 2026-10-30, after the owner messaged the bot without the `pair` prefix and only got the refusal. Outside a direct message six bare digits are a pairing attempt only when they are exactly the pending code (`src/Scout.Conversation/ConversationConductor.cs:201`), which burns it (see below). Other bare digits in a channel, or any with no code pending, are an ordinary message: they go to the allow-list and, for an allowed sender, to the model, they never count as a wrong code and they never burn the code.

  It succeeds only when the message is a direct message, the code equals `Scout:Access:PairingCode` and the code is pending (`src/Scout.Conversation/ConversationConductor.cs:204`). Success replies "Paired. Scout will answer you from now on."; every failure, whether wrong, expired, reused, burned, missing or outside a direct message, replies "That pairing code is not valid. Check the code in Scout's Settings and send it again." with no hint about which check failed.
- **Burning the code.** The correct code sent outside a direct message burns it, as `pair <code>` or bare, because others may have seen it; Settings tells the owner to send the bare code, so a paste into a channel must not leave a live code anyone could send first in a direct message. Bare digits outside a direct message never burn it unless the text is exactly the pending code. Five wrong codes in a row, from any sender, in either form (bare ones only count in a direct message), also burn it. A burned code is never accepted again in this process, and the replies stay the same, so a guesser learns nothing (`src/Scout.Conversation/ConversationConductor.cs:231`). Burning calls `IPairingSignal.Burned`. A failure to report it is logged as a warning and the reply is still the refusal. Any other text, such as `pair 482913 now` or seven digits, goes to the allow-list as before. The log line carries the channel id and accepted or refused, never the code, the text or the sender.
- **Direct message only.** `ChatMessage.IsDirectMessage` (`src/Scout.Conversation/Abstractions/ChatMessage.cs:45`) defaults to false. Discord sets it for a message without a guild (`src/Scout.Adapter.Discord/DiscordMessageTranslator.cs:60`), Slack for channel type `im` (`src/Scout.Adapter.Slack/SlackMessageTranslator.cs:85`), and Teams leaves it false, so Teams cannot pair.
- **Back to the desktop.** The conductor reports the sender through `IPairingSignal` (`src/Scout.Conversation/Access/IPairingSignal.cs:7`). The console host's `StdoutPairingSignal` writes `SCOUT_PAIRED=<userId>`, or `SCOUT_PAIRING_BURNED` for a burned code, to stdout, outside the logging pipeline like `SCOUT_READY` (`src/Scout.Host.Console/StdoutPairingSignal.cs:10`, registered at `src/Scout.Host.Console/HostComposition.cs:41`). The reply is sent only after that line is written; if the write fails, the user gets the failure reply, not "Paired.". Without a registered signal the code is refused.
- **Main adds the id.** The supervisor parses that marker on stdout only, never stderr (`ui/packages/main/src/worker/log-stream.ts:52`), and calls `paired` (`ui/packages/main/src/ipc/access.ts:124`). With no unexpired code pending, main ignores the line. Otherwise it clears the code before any await and adds the id through the same validation and storage as `scout:access.set`. A malformed id is rejected and nothing is stored. A stored change restarts the worker, which then has the id in its list and no code. On `SCOUT_PAIRING_BURNED` (`ui/packages/main/src/worker/log-stream.ts:54`) main drops the pending code (`ui/packages/main/src/ipc/access.ts:157`), so a restarted worker never gets it back. Settings, which watches the worker's stdout events, replaces the code with "Pairing expired. Start again." (`ui/packages/renderer/src/app/wizard/settings/settings.component.ts:388`).
- **Classification.** The pairing reply is a descriptive write in the originating direct message, like the refusal. The marker line goes to the desktop's own supervisor, and the id lands in Scout's own vault ([[pages/decisions/0009-descriptive-vs-executive-writes]]). The code is accepted only from the platform's sender id, so text in a message cannot pair someone else.
- **Known limits.** One worker process counts the failed attempts; a restart, such as a crash restart, starts the count at zero for a code main still holds. Five guesses in 10^6 codes give a guesser a 1 in 200,000 chance per code.

## Amendment 2026-11-04: the owner from chat

The channel operator's functions that list, read and change video projects from the chat answer only Scout's owner ([[pages/decisions/0033-channel-operator-architecture]], amendment of 2026-11-04). The allow-list says who may use Scout, not who the owner is, so a second list names the owner.

- **The setting.** `Scout:Access:OwnerUserIds`, platform user ids like `AllowedUserIds`, empty by default (`src/Scout.Conversation/Access/AccessOptions.cs:34`). An id is the owner only when it is also allowed, compared ordinally after trimming, blank entries ignored (`src/Scout.Conversation/Access/AccessPolicy.cs:48`). With the list empty no chat sender is the owner, whatever the allow-list says, so a platform fails closed; the local quick-ask user is always the owner ([[pages/decisions/0028-local-quick-ask-assistant]]).
- **Where it is read.** The conductor sets the owner flag of the turn from the platform's sender id when it enters the turn (`src/Scout.Conversation/ConversationConductor.cs:118`), and the operator reads it through `ChatOrigin.IsOwner` (`src/Scout.Conversation/Abstractions/ChatOrigin.cs:27`), never from text in the message or a function parameter.
- **Classification.** Like the allow-list, it never authorizes a write: it narrows who may ask the owner-only functions, and the owner's write grant for the feature still authorizes each write ([[pages/decisions/0014-per-request-identity-gating]], [[pages/decisions/0032-per-feature-write-permissions]]).
- **The desktop.** The desktop does not set the list yet; a "This is me" mark beside an id in "Who can use Scout" is a follow-up ([[pages/roadmap/followups]]). Until then the owner acts from the quick ask.
- **Enforcement.** `tests/Scout.Conversation.Tests/ConversationConductorOriginTests.cs:145` (the local owner), `:155` (an allowed sender only when listed), `:167` (no owner ids, open allow-list: nobody), `:175` (only each turn's own, false after the turn); `tests/Scout.Conversation.Tests/ConversationConductorAccessTests.cs:142` (listed and allowed, trimmed, ordinal).

## Consequences

- **Better:** nobody outside the list reaches the owner's GitHub token, clusters or plan; the default for a connected owner credential is closed, not open; the check runs before any model or plugin call, so a refused message costs nothing.
- **Worse:** after connecting GitHub, Kubernetes or the ChatGPT plan, Scout refuses everyone, the owner included, until the owner adds ids; the GitHub, Kubernetes and ChatGPT cards say so ("Nobody can use this connection from chat until you add allowed users in Settings."). Teams users must find their `29:` id, which Teams does not show.
- **Neutral but noteworthy:** an OpenAI or Gemini API key does not set `RequireAllowList`; with only an API-key provider and an empty list, everyone can use the bot and spend the owner's API credit. Changing the list restarts a running worker.

## Alternatives considered

- **Gate in each adapter.** Three copies of the rule, and a fourth for every new adapter; the conductor is the one place every adapter's handler goes through (`src/Scout.Host.Console/ConversationWorker.cs:45`).
- **Always fail closed on an empty list.** Breaks [[pages/invariants/local-first-default]]: a contributor with only local Ollama would have to configure ids before the first answer.
- **Owner-only answering just for the ChatGPT plan** (the original Phase 3 of [[pages/roadmap/chatgpt-plan-provider]]). Leaves GitHub and Kubernetes open; the allow-list covers all three.
- **Status quo.** Anyone in a channel with the bot reads what the owner's credentials can read.

## Enforcement

- `tests/Scout.Conversation.Tests/ConversationConductorAccessTests.cs:51` (refused user gets the exact text, router never called), `:80` (empty list plus required gives the setup text), `:94` (empty and not required still routes), `:110` (ordinal after trim), `:122` (refusal log carries the channel id and not the text).
- `tests/Scout.Conversation.Tests/ConversationServiceCollectionExtensionsTests.cs:140` resolves the conductor from configuration and asserts both refusals with no gateway call.
- `ui/packages/main/test/worker-build-env.test.ts:608`, `:617`, `:629` (GitHub, Kubernetes, ChatGPT plan each set `REQUIREALLOWLIST`), `:647` (none sets nothing).
- `ui/packages/main/test/ipc-access.test.ts:97` and the cases after it (validation, cap, de-duplication, restart), ending with a vault that fails to store: the change is rejected, the previous list stays and the worker is not restarted.
- Pairing: `tests/Scout.Conversation.Tests/ConversationConductorPairingTests.cs:75` (`pair <code>` in any case in a direct message replies Paired and reports the sender), `:89` (outside a direct message), `:102` (the correct code outside a direct message burns it), `:117` (five wrong codes burn it), `:139` (four wrong codes still let the correct one pair), `:156` (a failed burn report keeps the refusal), `:167` (wrong), `:179` (expired), `:193` (once), `:207` (no configured code), `:219` (no signal), `:234` (anything else goes to the allow-list), `:247` (a paired user not yet in the list is still refused), `:261` (failed report is not Paired), `:273` (logs carry no code, text or sender), `:291` (bare code in a direct message pairs), `:304` (five bare wrong codes burn it), `:323` (bare non-matching digits in a channel are an ordinary message and never count as wrong codes), `:343` (the bare correct code in a channel burns it), `:361` (bare digits with no code pending route normally), `:378` (the empty-list refusal explains how to pair); `tests/Scout.Conversation.Tests/ConversationServiceCollectionExtensionsTests.cs:112` (code and ISO expiry bind from `Scout:Access`); `tests/Scout.Host.Console.Tests/StdoutPairingSignalTests.cs:51` (exactly one `SCOUT_PAIRED=111` line), `:63` and `:85` (no line when wrong or reused), `:74` (one `SCOUT_PAIRING_BURNED` line); `ui/packages/main/test/ipc-access.test.ts:225` and the cases after it (code, expiry, env names, the marker from a supervisor adds the id and restarts, ignored with nothing pending or expired, invalid id rejected, a failing vault leaves the list and the worker alone, a burned code is dropped, code never logged); `ui/packages/main/test/worker-log-stream.test.ts:87` and `:101` (both markers on stdout only); `settings.component.spec.ts` (burned marker shows "Pairing expired. Start again.").
- Invariants: [[pages/invariants/no-executive-writes]], [[pages/invariants/secrets-never-in-source]], [[pages/invariants/local-first-default]].

## Related

- Services affected: [[pages/services/conversation]], [[pages/services/desktop-ui]]
- Related decisions: [[pages/decisions/0009-descriptive-vs-executive-writes]], [[pages/decisions/0014-per-request-identity-gating]], [[pages/decisions/0018-chatgpt-plan-sign-in]], [[pages/decisions/0019-github-app-device-flow]], [[pages/decisions/0020-kubernetes-from-local-kubeconfig]]
- Follow-ups: [[pages/roadmap/followups]]

## Notes

Out of scope for this decision: per-plugin permissions, roles, platform-qualified ids, and admin commands in chat other than sending the pairing code.
