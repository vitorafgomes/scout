---
title: "Conversation core - cumulative streaming sink and pump-owned drive loop"
version: "1.2"
date: 2026-05-29
updated: 2026-11-10
changes: "v1.2 (2026-11-10): amendment, bounded error endings: the terminal write of a reply that ended on an error gets what remains of the overall timeout or the terminal write grace, a rejected or unfinished write is logged once and the run returns, and error bodies fit the declared maximum body length."
page_type: adr
status: accepted
description: "Decision on the conversation core's cumulative streaming sink and pump-owned streaming across chat platforms."
source:
  - "owner-notes/06-decisions/2026-05-29-conversation-core-streaming-sink.md"
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
Task ProgressAsync(string activity, CancellationToken ct) => Task.CompletedTask;   // v1.1: what Scout is doing, before any text
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

## Amendment: progress before the text (2026-11-06)

The owner, after the first real code review from the desktop: "while it is waiting for the run, the agent should show the dots animation that something is being checked in the background". While the model reads a pull request file by file, nothing reached the sink until the first token, so the chat sat silent for a minute.

- `IResponseSink.ProgressAsync(activity, ct)` (`src/Scout.Conversation/Abstractions/IResponseSink.cs:36`) with a default body that ignores it: Discord, Slack and Teams change nothing; `VoiceReplySink` forwards it (`src/Scout.Conversation/Streaming/VoiceReplySink.cs:25`); the local adapter writes it as a `progress` event with the activity as `text` (`src/Scout.Adapter.Local/LocalResponseSink.cs:51`), never after the end. It sits between `Begin` and the terminal call and carries no text of the reply; the lifecycle of v1.0 is untouched.
- Who reports: `TurnProgress` (`src/Scout.Llm/Abstractions/TurnProgress.cs:8`), an async-local the conductor enters for the turn with the sink's `ProgressAsync` (`src/Scout.Conversation/ConversationConductor.cs:166`), the same shape as `ChatOrigin`; and `ActivityFilter` (`src/Scout.Llm/Guard/ActivityFilter.cs:13`), a kernel function-invocation filter registered for every kernel (`src/Scout.Llm/LlmServiceCollectionExtensions.cs:136`) that announces each function the model calls as a sentence with the short values of allow-listed argument names, "Get pull request file: octocat hello 42 src/Checkout.cs" (`:40`), never a body, a token, an unknown name, a long or a multi-line value; a report that fails never costs the function. Every plugin gets it without code: Kubernetes, Knowledge, YouTube, GitHub.
- On the desktop, main keeps the latest activity on the running turn and clears it on the next reply event ([[pages/services/desktop-quick-ask]]); the Chat screen and the quick-ask window show three dots with it, or "Working on it", while the reply is still empty.
- Enforcement: `tests/Scout.Llm.Tests/Guard/ActivityFilterTests.cs:33` (the sentence, outside a turn nothing, a report that throws, the scope ending with the turn), `tests/Scout.Conversation.Tests/ConversationConductorTests.cs:48` (a report during the turn reaches the sink and stops with it), `tests/Scout.Adapter.Local.Tests/LocalChatAdapterTests.cs:104` (the event between begin and update, a blank one skipped, none after the end).

## Amendment: bounded error endings (2026-11-10)

Scout's own audit of this repository found two gaps in the drive loop's endings: a reply that ended on a provider error could be longer than the body length the sink declared, and its terminal write ran on a token that never cancels, so a sink that hung held the run past the overall timeout. This amendment replaces the last sentence of the first Operational note under Consequences, which says `FailAsync` runs on `CancellationToken.None`; the note is left as written.

- **The token of an error ending.** The terminal write of a run that ended on an error, `CompleteAsync` with a rate-limit, quota, sign-in, key or vision sentence or `FailAsync`, gets what remains of the overall timeout, the smaller of `StreamPumpOptions.OverallTimeout` and the sink's `MaxResponseDuration`. Once that timeout has fired, which is how a timed-out stream reaches `FailAsync`, it gets `StreamPumpOptions.TerminalWriteGrace` (10 seconds by default, never negative), linked to the caller's token (`src/Scout.Conversation/Streaming/StreamPump.cs:243`, `src/Scout.Conversation/Streaming/StreamPumpOptions.cs:43`). Finalization is still not torn down by the cancellation that ended the stream; it now has a bound of its own.
- **The bound does not depend on the sink.** The pump stops waiting when the token cancels, whether or not the sink honours it; a write still running is left to finish on its own and a failure it ends in later is observed (`src/Scout.Conversation/Streaming/StreamPump.cs:254`, `:268`). The caller's cancellation stops the run and propagates, and a caller already cancelled gets no terminal call.
- **A write that is rejected or not finished.** It is logged once, as a Warning without the body, the exception's message or the exception, and the run returns normally; neither outcome reaches the conductor.
- **Error bodies fit the declared maximum body length.** The sentence is kept whole and the partial text gives way to it; `FailAsync` receives the first part of the partial text that fits (`src/Scout.Conversation/Streaming/StreamPump.cs:296`, `:281`). `RenderCapabilities` is unchanged: a sink that declares no limit, as Discord does, gets the text uncut.
- **Not covered.** `BeginAsync`, the updates and the completing write of a stream that ended well still end only when the sink honours their token, and the registered pump sets no overall timeout, so the bound above applies today only where a sink declares `MaxResponseDuration` ([[pages/roadmap/followups]]).
- Enforcement: `tests/Scout.Conversation.Tests/Streaming/StreamPumpFailureTests.cs:335` (the bodies), `:443`, `:474` and `:507` (the bound, with a sink that honours its token and one that ignores it), `:600` and `:584` (a rejected write). How it works now: [[pages/services/conversation]].

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
  (frozen under `src/old/` until 2026-11-06, now in the history before `93da0d4`).
