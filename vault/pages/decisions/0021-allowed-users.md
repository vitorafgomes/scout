---
title: "ADR 0021 - Only allowed users can use Scout once an owner credential is connected"
version: "1.0"
date: 2026-10-23
changes: "Initial ADR, accepted"
page_type: adr
status: accepted
description: "The conversation core answers only the platform user ids the owner lists in the desktop app; with an empty list it refuses everyone once GitHub, Kubernetes or the ChatGPT plan is connected, and answers everyone otherwise."
supersedes: ""
superseded_by: ""
source:
  - chat
  - src/Scout.Conversation/ConversationConductor.cs
  - src/Scout.Conversation/Access/AccessOptions.cs
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

Updated: 2026-10-23.

## Context

A security review on 2026-10-23 found that anyone who can message the bot drives the owner's credentials: the GitHub token ([[pages/decisions/0019-github-app-device-flow]]), the Kubernetes contexts the owner ticked, including `-admin` contexts of production clusters ([[pages/decisions/0020-kubernetes-from-local-kubeconfig]]), and the owner's ChatGPT plan ([[pages/decisions/0018-chatgpt-plan-sign-in]]). The plugins only read, but what they read (private repositories, cluster state) is disclosed to whoever asks, and plan usage counts against the owner's limits. The owner approved restricting who can use Scout before the desktop-connections PR. [[pages/invariants/local-first-default]] requires that a contributor with local Ollama and no credentials keeps working with zero configuration.

## Decision

- **Gate in the conversation core.** `ConversationConductor.HandleAsync` checks `ChatMessage.UserId` against `Scout:Access` before it logs the routing line, routes, pumps or calls the model (`src/Scout.Conversation/ConversationConductor.cs:60`, rule at `:101`). `AccessOptions` holds `AllowedUserIds` and `RequireAllowList` (`src/Scout.Conversation/Access/AccessOptions.cs:8`), bound in `AddScoutConversation` (`src/Scout.Conversation/ConversationServiceCollectionExtensions.cs:33`). Ids are compared ordinally after trimming; blank entries are ignored.
  - A non-empty list without the sender: the reply is exactly "Scout only answers its configured users."
  - An empty list with `RequireAllowList = true`: the reply is exactly "Scout has no allowed users yet. The owner can add them in the Scout desktop app under Settings."
  - Otherwise nothing changes.
  - A refusal opens the sink and completes it with that text and no citations, then returns; the router, the stream pump, the LLM and every plugin stay untouched. It logs one Information line with the channel id only, never the message text or the sender id.
- **Fail closed only when an owner credential is connected.** The desktop sets `SCOUT__ACCESS__REQUIREALLOWLIST=true` when GitHub is connected, a Kubernetes selection exists, or `llm.provider` is `ChatGptPlan` (`ui/packages/main/src/worker/build-env.ts:58`). With none of these and an empty list the variable is absent, so a fresh clone, a contributor on local Ollama, and a desktop install with only an API-key provider behave as before.
- **The owner manages the list in the app.** Settings has a "Who can use Scout" section with an id field, Add, a list with Remove, and a line on where each platform shows a user id. Main validates every list it receives (`ui/packages/main/src/ipc/access.ts:37`): each id trimmed, 1 to 64 characters from `[A-Za-z0-9._:@-]`, de-duplicated, at most 50; one bad id rejects the whole list. The list is stored under the main-only vault key `access.allowed-users` (not a secret, but the renderer only reaches it through `scout:access.get` and `scout:access.set`), reaches the worker as `SCOUT__ACCESS__ALLOWEDUSERIDS__<n>` (`ui/packages/main/src/worker/build-env.ts:55`), and a running worker restarts when the list changes.
- **Ids are platform user ids, not platform-qualified.** Discord passes the author snowflake (`src/Scout.Adapter.Discord/DiscordMessageTranslator.cs:60`), Slack the member id (`src/Scout.Adapter.Slack/SlackMessageTranslator.cs:81`), Teams `activity.From.Id` (`src/Scout.Adapter.Teams/TeamsMessageTranslator.cs:122`), which is the Bot Framework user id starting with `29:`, not the Microsoft Entra object id. ponytail: an id listed for one platform also admits the same string on another; a collision between a Discord snowflake, a Slack `U...` id and a Teams `29:...` id is theoretical. Qualify ids as `Adapter:UserId` when a second adapter ships in the desktop app.
- **Classification.** The refusal is a descriptive write in the originating channel, through the same sink as any reply ([[pages/decisions/0009-descriptive-vs-executive-writes]]). Identity here decides who receives replies at all; like [[pages/decisions/0014-per-request-identity-gating]], it never authorizes a write. The user id comes from the platform event, so text in a message cannot change it.

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
- `tests/Scout.Conversation.Tests/ConversationServiceCollectionExtensionsTests.cs:114` resolves the conductor from configuration and asserts both refusals with no gateway call.
- `ui/packages/main/test/worker-build-env.test.ts:466`, `:475`, `:487` (GitHub, Kubernetes, ChatGPT plan each set `REQUIREALLOWLIST`), `:496` (none sets nothing).
- `ui/packages/main/test/ipc-access.test.ts:72` and the cases after it (validation, cap, de-duplication, restart), ending with a vault that fails to store: the change is rejected, the previous list stays and the worker is not restarted.
- Invariants: [[pages/invariants/no-executive-writes]], [[pages/invariants/secrets-never-in-source]], [[pages/invariants/local-first-default]].

## Related

- Services affected: [[pages/services/conversation]], [[pages/services/desktop-ui]]
- Related decisions: [[pages/decisions/0009-descriptive-vs-executive-writes]], [[pages/decisions/0014-per-request-identity-gating]], [[pages/decisions/0018-chatgpt-plan-sign-in]], [[pages/decisions/0019-github-app-device-flow]], [[pages/decisions/0020-kubernetes-from-local-kubeconfig]]
- Follow-ups: [[pages/roadmap/followups]]

## Notes

Out of scope for this decision: per-plugin permissions, roles, platform-qualified ids, and admin commands in chat.
