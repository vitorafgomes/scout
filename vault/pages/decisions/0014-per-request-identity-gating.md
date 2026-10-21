---
title: "ADR - Per-request identity and disclosure gating (SelfInfo Path B)"
version: "1.0"
date: 2026-07-05
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: adr
status: accepted
description: "Plugins run inside a singleton Semantic Kernel with singleton KernelPlugin instances, so they had no way to know *who* was asking or *where*."
source:
  - "obsidian-vault/scout/06-decisions/2026-07-05-per-request-identity-gating.md"
reliability: medium
---

# ADR - Per-request identity and disclosure gating (SelfInfo Path B)

## Context

Plugins run inside a **singleton** Semantic Kernel with **singleton** `KernelPlugin`
instances, so they had no way to know *who* was asking or *where*. `Scout.Plugin.SelfInfo`
therefore reported the same redacted host facts to every chat user. "Path B" is the
infrastructure to carry per-request chat identity into the kernel invocation so a
plugin can gate sensitive output — the prerequisite for admin/DM-gated SelfInfo detail
and for any future per-user plugin. See [[pages/architecture/selfinfo-plugin-survey]].

## Decision

**Transport: an `AsyncLocal`-backed ambient context** (`IScoutRequestContext` in the
new leaf project `Scout.Contracts`), set once per inbound message in
`ConversationConductor.HandleAsync` around the router call and the streaming pump.

Rejected alternatives:

- **KernelArguments** — the default direct-chat path (`GetStreamingChatMessageContentsAsync`)
  exposes no `KernelArguments` argument; only the agent path does, so it is not a uniform
  transport. Worse, anything the model can *see* it can *forge* — identity used for a
  security gate must never be a model-visible function parameter.
- **Scoped DI** — the conductor, router, gateway, kernel and plugins are all singletons
  resolved once; there is no per-request scope, and a scoped service would be captured at
  construction.

`AsyncLocal` works with the existing singleton wiring, flows across
`async`/`await`/`await foreach`/`IAsyncEnumerable`, and is **not** dropped by
`ConfigureAwait(false)` (that drops `SynchronizationContext`, not `ExecutionContext`).

**Identity contract:** `ScoutRequestIdentity(UserId, ChannelId, IsDirectMessage, AdapterName)`.
The DM flag already existed in every adapter translator and was discarded; it is now
carried on `ChatMessage` (optional trailing params) and the translators pass it.

**Gating:** `SelfInfoOptions.AdminUserIds` + `DmOnly`. Disclosure resolves **fail-closed**:
no identity, no configured admins, a non-admin caller, or (with `DmOnly`) a non-DM caller
all yield **today's exact redacted output**. Only an admin — in a DM when `DmOnly` — gets
the appended **medium** tier (real install/config paths, machine short-name, exact
versions/counts, project names). The **never** tier (secret values/presence, hostname/FQDN,
IPs/MACs/interfaces, endpoint URLs/ports, OS build/kernel) stays redacted unconditionally,
even for an admin in a DM. `DmOnly` is an AND-constraint on admin, not an independent unlock.

## Load-bearing assumption (proven, not assumed)

The lazy `ChatResponse` must be enumerated *inside* the conductor's ambient scope, because
that is where SK invokes plugin functions. Verified by a test that reads
`IScoutRequestContext.Current` at enumeration time through the **real** `StreamPump`
(behind `Task.Yield` + `ConfigureAwait(false)`) and asserts it equals the message identity,
then `null` after `HandleAsync` returns. If this ever regresses (a consumer enumerates the
response after `HandleAsync` returns), the failure mode is **fail-closed** — identity reads
`null` and output falls back to redacted, never a leak.

## Security invariant

Identity here is a **confidentiality control on output disclosure only** — it must never be
repurposed to authorize an executive write. This stays within the descriptive-vs-executive
boundary ([[pages/decisions/0009-descriptive-vs-executive-writes]]). It is injection-resistant: the
ambient `UserId`/`IsDirectMessage` come from the platform event, not the message text, so a
prompt-injection can make the model *call* a function but cannot change *who the identity
says is asking*.

## Consequences

- Every registration path uses `TryAddSingleton` so exactly one accessor instance is shared
  between the setter (conductor) and readers (plugins); two instances would silently
  fail-closed. Asserted by the Host.Console composition test.
- New `Scout.Contracts` leaf project referenced by `Scout.Conversation` and
  `Scout.Plugin.SelfInfo` (no cycle; adapters need no new reference).
- `UserId` is unique only within one platform; `AdapterName` is carried so admin lists can be
  qualified (`Adapter:UserId`) later. v1 matches plain `UserId`.

## Status

Implemented on branch `feat/selfinfo-path-b` (PR #16). Solution builds 0/0; full suite green.

## Cross-refs

[[pages/architecture/selfinfo-plugin-survey]] · [[pages/decisions/0009-descriptive-vs-executive-writes]] · handoff-2026-07-04 (private note)
