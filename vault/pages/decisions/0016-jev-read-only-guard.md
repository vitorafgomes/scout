---
title: "ADR 0016 - TypeSafe's Jev as a read-only guard in front of the model"
version: "1.1"
date: 2026-10-23
changes: "State sent to Jev carries recent turns; default pooled connection lifetime pinned by a test; disconnect from the card"
page_type: adr
status: accepted
description: "When a TypeSafe key is set, Jev judges each latest user message, in the context of the turns before it, before the model runs and Scout refuses the ones that ask it to change something; with no key nothing changes."
supersedes: ""
superseded_by: ""
source:
  - chat
  - src/Scout.Llm/Guard/
  - src/Scout.Llm/LlmServiceCollectionExtensions.cs
  - ui/packages/main/src/worker/build-env.ts
  - ui/packages/renderer/src/app/sources/sources.component.ts
reliability: high
---

# ADR 0016 - TypeSafe's Jev as a read-only guard in front of the model

> With `Scout:Guard:TypeSafe:ApiKey` set, the kernel's chat service is a decorator that asks TypeSafe's Jev whether the latest user message, read with the turns before it, asks Scout to change something; at or above the threshold the model is never called and Scout replies with a fixed refusal.

## Status

Accepted on 2026-10-23 at the owner's request.

## Context

On 2026-10-23 the owner asked for a Jev integration, with everything kept inside Semantic Kernel ([[pages/decisions/0012-llm-communication-through-semantic-kernel]]), and chose its role: a read-only guard that judges each request before the model runs. Until now the read-only contract ([[pages/decisions/0009-descriptive-vs-executive-writes]]) held by omission only: no write function exists, and nothing at run time looked at what the user asked for ([[pages/invariants/no-executive-writes]]).

Jev is TypeSafe's "System One" model. It does not generate text; it answers typed questions with a probability. Its API (`POST /v1/systemone`, bearer key) is not OpenAI-compatible, and there is no official .NET SDK and no Semantic Kernel connector, so Scout carries a minimal client of its own for the one question type it uses.

## Decision

- **Configuration.** `Scout:Guard:TypeSafe` binds `TypeSafeGuardOptions` (`src/Scout.Llm/Configuration/TypeSafeGuardOptions.cs:7`): `ApiKey` (empty), `Model` (`jev-latest`), `Endpoint` (`https://api.typesafe.ai`), `Threshold` (0.5). An empty key registers nothing: no options, no HTTP client, no decorator.
- **Client.** `TypeSafeClient` (`src/Scout.Llm/Guard/TypeSafeClient.cs:17`) is a typed `HttpClient` with a 5 s timeout. The guard is a singleton that holds the client for the process lifetime, so factory handler rotation never reaches it; Scout relies on the `IHttpClientFactory` default primary handler on .NET 10, a `SocketsHttpHandler` whose pooled connections live 2 minutes, and `ReadOnlyGuardCompositionTests` pins that default. It asks one `noul` question named `executive_write` and returns P(yes). Every failure (non-success status, timeout, connection fault, unreadable body) becomes a `TypeSafeException` whose message carries the status and, for a 422, the provider's validation messages; never the key and never the raw body.
- **Guard.** `ReadOnlyGuardChatCompletionService` (`src/Scout.Llm/Guard/ReadOnlyGuardChatCompletionService.cs:17`) wraps the provider's `IChatCompletionService`. For the streaming and non-streaming calls it sends a structured state: `recent_turns`, up to 12 user and assistant messages (six exchanges, the default conversation memory) before the latest user message, oldest first, each cut to its last 1000 characters so an offer at the end of a long reply survives, and `latest_user_message`, cut to its last 4000 characters. System and tool messages are never sent. The instructions tell Jev to judge the latest message in the context of the recent turns, so "ok, do it" after a proposed restart counts as a change request; sending the last message alone let that confirmation through. At p >= threshold it does not call the inner service and returns one assistant message: "Scout is read-only: it can explain, summarise and draft, but it will not change code, infrastructure, configuration, data or accounts. If you want a change, Scout can describe the steps for you to apply." It logs `Read-only guard refused a request (p=0.90)` at Information, without the message text. Below the threshold it delegates with the same history, settings and kernel.
- **Fail-open.** When the TypeSafe call fails, the guard logs a Warning with the status and lets the request through to the model. A guard outage must not take chat down.
- **One seam, inside the kernel.** `AddScoutLlm` swaps the kernel's single chat-service registration for the decorator before the kernel is built (`src/Scout.Llm/LlmServiceCollectionExtensions.cs:161`) and removes the connector's separate `ITextGenerationService`, so no unguarded chat path remains in the kernel. The direct gateway path and the Project Expert agent both resolve the chat service from that kernel, so both are covered.
- **Desktop.** The AI Sources screen has a "TypeSafe (Jev)" card that stores the key under vault key `guard.typesafe-api-key`; the main process maps it to `SCOUT__GUARD__TYPESAFE__APIKEY` for every LLM provider. The card does not change `llm.provider`.

## Consequences

- **Better:** the first run-time check of the read-only contract. A request such as "delete the release branch" is refused before any model or tool runs.
- **Worse:** with a key set, every latest user message and up to 12 messages before it leave the machine to TypeSafe. Those earlier turns include Scout's own replies, which can quote repository content found through `search_project` or the GitHub plugin. Each user message costs one extra round trip, up to 5 s; a tool round trip inside the model turn does not add another.
- **Worse:** a false positive refuses a legitimate question; the threshold is the only knob.
- **Neutral but noteworthy:** a model's judgment is defence in depth, not proof. The read-only contract still rests on no write function existing; fail-open means a TypeSafe outage removes the check silently, apart from the Warning.
- **Reversible from the desktop:** the key can be removed from the card; the guard is then off.
- **Local-first holds:** with no key, the kernel's chat service is the provider's own and the worker environment is unchanged ([[pages/invariants/local-first-default]]).

## Alternatives considered

- **Fail closed when TypeSafe fails.** Rejected for now: a third-party outage would stop every answer. Revisit if the guard becomes the only line of defence.
- **Guard in `ChatGateway` instead of the kernel.** Rejected: anything resolving the chat service from the kernel (the agent, a future prompt function) would bypass it, and the owner asked for everything inside Semantic Kernel.
- **A community TypeSafe SDK.** Rejected under Rule C: one endpoint and one question type are a few lines.
- **Guarding tool calls too.** Deferred; see [[pages/roadmap/followups]].

## Enforcement

- `tests/Scout.Llm.Tests/Guard/ReadOnlyGuardChatCompletionServiceTests.cs` - refusal at p=0.9 and at exactly the threshold with the model never called, pass-through at p=0.1, fail-open on a 500, the state shape (12 prior messages, tail truncation, latest capped at 4000), no system or tool text, an offer at the end of a long reply reaching Jev, a "ok, do it" refused because the earlier proposal is sent, and the exact question and criteria sent to Jev.
- `tests/Scout.Llm.Tests/Guard/TypeSafeClientTests.cs` - request shape and bearer key; 422, 500, 401, timeout and reset raise `TypeSafeException` without the key.
- `tests/Scout.Llm.Tests/Guard/ReadOnlyGuardCompositionTests.cs` - with a key the kernel's chat service is the guard, both the direct and the agent gateway paths return the refusal, and one user message whose model turn runs a tool costs exactly one Jev call.
- `tests/Scout.Llm.Tests/LlmServiceCollectionExtensionsTests.cs` - with a key, every one of the eight providers' kernels holds the guard as its chat service and no `ITextGenerationService` or `IChatClient`; with no key the kernel keeps the provider's own service.
- Invariant page: [[pages/invariants/no-executive-writes]].

## Related

- Services affected: [[pages/services/llm]], [[pages/services/desktop-ui]]
- Related invariants: [[pages/invariants/secrets-never-in-source]], [[pages/invariants/pluggable-boundaries]]
