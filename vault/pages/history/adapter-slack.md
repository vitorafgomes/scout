---
title: "Adapter.Slack - history"
version: "1.0"
date: 2026-10-21
changes: "Initial history note: SlackNet 0.19.0 bump"
page_type: reference
status: artefact
description: "What changed in the Slack adapter and when."
source:
  - chat
  - src/Scout.Adapter.Slack/SlackChatAdapter.cs
  - tests/Scout.Adapter.Slack.Tests/SlackChatAdapterTests.cs
reliability: high
---

# Adapter.Slack - history

> Dated changes to [[pages/services/adapter-slack]]; the service page states how it works now.

## 2026-10-21 - SlackNet 0.17.10 to 0.19.0

- **What changed:** `ISlackSocketModeClient.Disconnect()` is obsolete in 0.19.0 (`CS0618`, an error under `TreatWarningsAsErrors`). `SlackChatAdapter.StopAsync` now awaits `DisconnectAsync()`, which returns only once the socket connection work has stopped, bounded by the caller's cancellation token through `WaitAsync`. `DisposeAsync` runs its `StopAsync` call inside `try`/`finally`, so the lifecycle and the socket client are disposed even when stop throws; the socket client is released with `await DisposeAsync()`, because in 0.19.0 its synchronous `Dispose()` blocks a thread-pool thread on the pending disconnect.
- **Behaviour:** stop no longer returns before the socket is down unless the host shutdown token is cancelled first (then it throws `OperationCanceledException`), and a fault from `DisconnectAsync()` propagates out of `StopAsync` and out of `DisposeAsync` (the lifetime token is already cancelled and the handler cleared, so no further message reaches the handler).
- **Tests:** `StopAsync_Disconnects_Socket_Mode` asserts `DisconnectAsync`; `StopAsync_Waits_For_Socket_Mode_Disconnect_To_Finish`, `StopAsync_When_Socket_Mode_Disconnect_Fails_Surfaces_The_Fault_And_Stops_Handling`, `StopAsync_When_Socket_Mode_Disconnect_Never_Completes_Returns_On_Cancellation` and `DisposeAsync_When_Socket_Mode_Disconnect_Fails_Still_Disposes_The_Socket_Client` are new, in `tests/Scout.Adapter.Slack.Tests/SlackChatAdapterTests.cs`.
- **Not changed:** `ConversationWorker` still stops adapters in sequence without per-adapter fault isolation, so a Slack disconnect fault stops the adapters after it from being stopped; tracked in [[pages/roadmap/followups]].
- **Invariants:** [[pages/invariants/pluggable-boundaries]] holds; SlackNet types stay inside the adapter project and its tests.
