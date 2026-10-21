---
title: "Conversation core - cumulative streaming sink and pump-owned drive loop"
version: "1.0"
date: 2026-05-29
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: adr
status: accepted
description: "Decision on the conversation core's cumulative streaming sink and pump-owned streaming across chat platforms."
source:
  - "obsidian-vault/scout/06-decisions/2026-05-29-conversation-core-streaming-sink.md"
reliability: medium
---

# Conversation core - cumulative streaming sink and pump-owned drive loop

## Context

Scout is multi-platform chatops: Discord now, Slack and Teams next. The
streaming-first mandate (feedback-scout-streaming-first (private note)) means every
assistant response streams to the chat surface as tokens arrive. Each platform
renders a growing message differently:

- **Discord** (`Discord.Net`): post an embed, `ModifyAsync` repeatedly — fine-grained edit.
- **Slack** (`SlackNet`): `chat.update` repeatedly — edit, different rate limits.
- **Teams** (`Microsoft.Bot.Framework`): the least capable. Updates must carry
  **cumulative** text (not deltas), ~1 req/s, a 2-minute stream cap, citations
  only on the final message, 1:1 chats only, no fine-grained edit of interim
  content.

In the archived design (`src/old/`), the stream-drive loop (the `await foreach`
over tokens + throttle + truncate + finalize + rate-limit retry) lived **inside
each adapter's poster** (`Scout.Adapter.Discord/Streaming/EmbedEditThrottler.cs`
+ `DiscordEmbedReplyPoster.cs`). Every new platform would re-implement that loop.
Routing also returned a dual-mode `ChatResponse` (plain XOR streaming) that each
adapter had to branch on.

## Decision

Introduce `src/Scout.Conversation`, a platform-agnostic core that owns the
stream-drive loop once and renders through a thin per-platform sink. The sink
contract is designed against the Teams least-common-denominator so the routing
layer never knows a platform's capabilities.

### Outbound sink — `IResponseSink` (cumulative)

```
RenderCapabilities Capabilities { get; }
Task BeginAsync(CancellationToken ct);
Task UpdateAsync(string cumulativeText, CancellationToken ct);   // CUMULATIVE, not a delta
Task CompleteAsync(string finalText, IReadOnlyList<Citation> citations, CancellationToken ct);
Task FailAsync(string finalTextSoFar, string reason, CancellationToken ct);
```

- Lifecycle: exactly one `Begin`, zero+ monotonic-cumulative `Update`, then
  exactly one terminal `Complete` or `Fail`.
- `UpdateAsync` takes **cumulative text** precisely because Teams forbids deltas;
  Discord/Slack re-render the whole body anyway, so cumulative costs them nothing.
  This single decision is what lets the router/pump stay capability-ignorant.
- `RenderCapabilities`: `FineGrainedEdit`, `MaxBodyLength`,
  `SupportsInterimCitations`, `EditIntervalFloor`, and `MaxResponseDuration`
  (the Teams 2-minute cap, declared by the sink — see Consequences).

### The drive loop — `StreamPump` (owned once)

`StreamPump.RunAsync(ChatResponse, IResponseSink, ct)` owns: `Begin`, the
`await foreach` over `ChatResponse.Tokens`, cumulative buffering, throttled
`UpdateAsync` at a cadence derived from `Capabilities` (Discord ~750ms, Teams
~1s floor), tail/ head truncation to `MaxBodyLength`, and the terminal
`Complete`/`Fail`. Capability-driven degradation: a non-fine-grained,
non-interim sink gets final-only rendering. The pump is the single caller and is
transport-agnostic — it contains **no** platform-specific code.

### Inverted `IChatAdapter`

```
Task StartAsync(Func<ChatMessage, IResponseSink, CancellationToken, Task> handler, CancellationToken ct);
Task StopAsync(CancellationToken ct);
string Name { get; }
```

The adapter normalizes inbound platform events to `ChatMessage`, constructs an
`IResponseSink` bound to the **originating channel only**, and invokes the
handler with a lifetime-linked cancellation token. The core
(`ConversationConductor`) routes and pumps. Ownership is inverted: **adapters
provide a sink, the core drives it.** The descriptive-write invariant is enforced
by construction — the sink exposes no API to post outside its channel, and the
core never sees a channel handle.

### Routing and composition

- `IConversationRouter.Route(ChatMessage) -> ChatResponse` — non-null, always
  streaming. v1 has one responder, `LlmStreamingRouter`, which consumes
  `IChatGateway` (NOT `IChatCompletionService` directly), staying SK-free per
  [[pages/decisions/0012-llm-communication-through-semantic-kernel]].
- `ChatResponse` redesigned to `(IAsyncEnumerable<string> Tokens,
  IReadOnlyList<Citation> Citations)` + `FromText` for the forced-non-streaming
  path. The plain-XOR-streaming union is gone.
- `ChatMessage`, `ChatResponse`, `Citation`, `IChatAdapter` relocated from
  `Scout.Llm.Abstractions` to `Scout.Conversation.Abstractions` (they are
  chat-transport, not LLM, concerns). `Scout.Conversation` depends on
  `Scout.Llm`; never the reverse.
- `ConversationConductor` wraps route+pump in an OpenTelemetry activity and
  guarantees a routing/streaming failure surfaces as `FailAsync`, never a host
  crash.

### Rate-limit resilience belongs to the transport (the sink), not the core

429/5xx detection and backoff live in the sink that owns the platform SDK's
typed exception (e.g. `DiscordResponseSink` catches `Discord.Net`'s
`RateLimitedException` and applies Retry-After backoff). The pump simply calls
`UpdateAsync`; a transient interim failure is swallowed and the buffer retained.
This keeps the core free of platform-specific rate-limit reflection.

## Alternatives considered

- **Per-adapter drive loop (the old design).** Rejected: every platform
  re-implements throttle/truncate/finalize/retry; bug surface multiplies.
- **Delta-based sink updates.** Rejected: Teams requires cumulative text;
  delta-first would force a Teams-specific re-accumulation layer and leak Teams
  into the core.
- **Rate-limit handling in the core pump (duck-typed reflection).** Tried in the
  first cut; rejected on review — it was Discord-shaped, would not detect Slack/
  Teams HTTP-header 429s, and pulled `System.Reflection` into a platform-agnostic
  type. Moved into the sink.
- **A core `SinkLifecycleGuard` decorator.** Deferred (YAGNI): each sink enforces
  its own Begin/Update/terminal ordering for v1; revisit if a third-party sink
  author needs the invariant for free.

## Consequences

### Positive

- **One drive loop.** Slack and Teams sinks implement only Begin/Update/Complete/
  Fail + `Capabilities`; the pump/router/conductor are unchanged. Verified by a
  final-only-degradation test for the non-edit path.
- **Teams fits without touching the core.** `MaxResponseDuration` lets the Teams
  sink declare its 2-minute cap; the pump folds it as
  `min(OverallTimeout, MaxResponseDuration)`, mirroring the `EditIntervalFloor`
  fold. Cumulative semantics are the contract default.
- **Streaming-first end to end**, SK-free router, correct layering, no cycle.
- **Descriptive-write boundary enforced by construction**, not convention.

### Negative

- **Cumulative re-send cost.** Edit-based platforms re-transmit the full body per
  update. Bounded by the throttle cadence and `MaxBodyLength`; acceptable.
- **Capability surface must stay honest.** New platforms must set
  `RenderCapabilities` correctly or get wrong cadence/degradation. Mitigation:
  the capability contract is small and documented; per-sink tests assert behavior.

### Operational

- The pump's `OverallTimeout` now bounds `BeginAsync` and the terminal
  `CompleteAsync` (timeout-linked token), important for non-edit platforms whose
  only write is the terminal call. `FailAsync` runs on `CancellationToken.None`
  so finalization is not torn down by the same cancellation.
- Each platform transport is an external boundary: mandatory failure-injection
  tests (429 retry, 5xx drop, gateway-throws-mid-stream, cancellation) — present
  for Discord, required for Slack/Teams when built.
- Quality gates run: `architect-reviewer` (cross-cutting contracts),
  `code-reviewer` (each adapter). Both passed for the conversation core + Discord
  reference adapter (APPROVE-WITH-NITS; the four architecture majors were fixed).

## Status / sequencing

Accepted and implemented for the conversation core + Discord reference adapter on
branch `feat/scout-llm-gateway` (131 tests green). Slack and Teams adapters
implement this same `IResponseSink` and are the next parallel workstream,
alongside the gRPC backend↔frontend transport
([[pages/decisions/0011-backend-frontend-grpc-uds]]).

## References

- LLM transport: [[pages/decisions/0012-llm-communication-through-semantic-kernel]].
- Streaming-first mandate: feedback-scout-streaming-first (private note).
- Descriptive-write invariant: [[pages/decisions/0009-descriptive-vs-executive-writes]].
- Backend↔frontend transport: [[pages/decisions/0011-backend-frontend-grpc-uds]].
- Generalized from the archived `Scout.Adapter.Discord/Streaming/EmbedEditThrottler.cs`
  (frozen under `src/old/`).
