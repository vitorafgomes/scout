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
  - src/Scout.Adapter.Slack/SlackAdapterLifecycle.cs
  - tests/Scout.Adapter.Slack.Tests/SlackChatAdapterTests.cs
reliability: high
---

# Adapter.Slack - history

> Dated changes to [[pages/services/adapter-slack]]; the service page states how it works now.

## 2026-11-11 - A failed start leaves the adapter stopped, and a stop during the start wins

- **Asked for:** an automated audit of main (issue #266, "Failed or stopped startup can leave the adapter running"): the start armed the lifecycle before `auth.test` and the connect with no rollback, and a stop landing during `auth.test` was followed by the handler and the connect.
- **What changed:** `StartAsync` runs `auth.test` and the connect inside one try; a failure stops the adapter and rethrows. After `auth.test` the start takes a connect gate and reads the lifecycle again, connecting nothing once a stop has run; `StopAsync` takes the same gate before it disconnects, the shape the Discord adapter already had.
- **Tests:** `A_Start_Whose_Auth_Or_Connect_Fails_Leaves_The_Adapter_Stopped` (the Rule I test: auth.test and connect failing), red with the rollback removed; `A_Stop_That_Arrives_During_Start_Wins_And_Leaves_No_Connection_Open` (auth and connect phases), red with the lifecycle check removed and, for the connect phase, with the stop's gate removed.
- **Then, from a performance review of the branch:** the connect ran on the start token while it held the gate, and the lifetime token is linked from the start token, so the stop's cancel never reached a slow or hung connect; the stop waited on the gate until its own token fired (the host shutdown timeout) and threw out of `ConversationWorker.StopAsync`, leaving the adapters after Slack unstopped. The connect now runs on the lifetime token, and a connect that a stop cancelled, the start token not cancelled, makes the start return like a stop that lands during `auth.test`; a start token cancelled by the host still throws. `A_Stop_That_Arrives_While_The_Connect_Is_Still_Pending_Returns_Promptly_And_Leaves_The_Adapter_Stopped` is red with the start token put back on the connect (the stop does not return within its 10 s bound) and red with the cancelled connect rethrown (the start ends cancelled).

## 2026-11-10 - The running state and the lifetime token read in one step

- **Asked for:** a defect seen while fixing the Teams finding "Lifecycle state and handler publication are not atomic": a message read the running state, then the lifetime token, outside the turn's try; a stop landing between the two made the token getter throw (no token, or a disposed one) into SlackNet's event handler.
- **What changed:** `SlackAdapterLifecycle` takes one lock for start, stop and a new `TryGetLifetimeToken`, the start creates the token before the started state is visible, and the stop clears the token in the lock before it cancels; `OnMessageReceivedAsync` reads the token through `TryGetLifetimeToken` once. The handler is still armed after `auth.test`, as before.
- **Tests:** `The_Lifetime_Token_Is_Handed_Out_Only_While_Running_And_A_Read_Before_Start_Or_After_Stop_Answers_False_Without_Throwing` in `SlackAdapterLifecycleTests.cs`, red while the read was a stub that answered false, and red again with the read answering running whatever the state; `A_Message_While_Running_Reaches_The_Handler_Of_That_Start_And_One_After_Stop_Reaches_None_And_Never_Throws` in `SlackChatAdapterTests.cs`, through the adapter, red with the adapter's read inverted, and green with the adapter's turn path put back to the old two-step read; `A_Start_Whose_Auth_Test_Fails_Surfaces_The_Fault_Never_Connects_Hands_No_Message_To_The_Handler_And_Still_Stops`, the Rule I test of the start, red with the handler armed before `auth.test`. Proved by a test: the adapter takes the token from the lifecycle's one-step read, and a message after stop is dropped without a throw (the stop also clears the handler, so either guard alone drops it). `A_Message_A_Stop_Overtakes_Is_Dropped_Quietly_And_Never_Throws` in `SlackChatAdapterTests.cs` stops the adapter from `BeforeSessionRead`, an internal hook the lifecycle runs at the start of `TryGetLifetimeToken` (null outside tests, reached through an internal `Lifecycle` accessor on the adapter), and asserts that the message is dropped without a throw and never reaches the handler. It is red on the code before the fix, with the same hook at the start of the `LifetimeToken` getter, between the running check and the token read: the getter threw `InvalidOperationException` ("Lifecycle not started.") out of `OnMessageReceivedAsync`. A hook inside the stop cannot make the old code red, because the old stop changed the state before it touched the token and the old turn checked the state first. Proved by a test: a message a stop overtakes is dropped quietly and never throws. Proved only by reading the code: the lock against a stop on another thread during the read, and the start creating the token before the started state is visible.

## 2026-11-09 - A terminal call before a successful Begin throws

- **Asked for:** Scout's own audit of this repository (issue "Terminal calls can silently discard the response"): `CompleteAsync` and `FailAsync` made before a Begin that posted marked the sink terminal, found no message to edit and returned as if the reply had landed.
- **What changed:** the sink's terminal guard throws `InvalidOperationException` "Terminal call before a successful BeginAsync." when no message was posted, whether Begin was never called or Slack refused it, matching the update guard. The pump never makes a terminal call after a failed Begin; the conductor's catch-all `FailAsync` does, and it already logs a throwing `FailAsync` as a Warning and goes on.
- **Tests:** `A_Terminal_Call_Before_Begin_Throws_Like_An_Update_Instead_Of_Discarding_The_Response` and `A_Terminal_Call_After_A_Begin_Slack_Refused_Throws_Instead_Of_Reporting_Success` (complete and fail each) in `tests/Scout.Adapter.Slack.Tests/Streaming/SlackResponseSinkTests.cs`; `A_Sink_Whose_Begin_And_Fail_Both_Throw_Never_Crashes_The_Host` in `tests/Scout.Conversation.Tests/ConversationConductorTests.cs`.

## 2026-11-08 - SlackNet 0.19.0 to 0.20.0

- **What changed:** nothing in the adapter. 0.20.0 removes `ISlackTypeResolver` and its builder hooks, which the adapter never used; it builds and its tests pass unchanged.

## 2026-10-21 - SlackNet 0.17.10 to 0.19.0

- **What changed:** `ISlackSocketModeClient.Disconnect()` is obsolete in 0.19.0 (`CS0618`, an error under `TreatWarningsAsErrors`). `SlackChatAdapter.StopAsync` now awaits `DisconnectAsync()`, which returns only once the socket connection work has stopped, bounded by the caller's cancellation token through `WaitAsync`. `DisposeAsync` runs its `StopAsync` call inside `try`/`finally`, so the lifecycle and the socket client are disposed even when stop throws; the socket client is released with `await DisposeAsync()`, because in 0.19.0 its synchronous `Dispose()` blocks a thread-pool thread on the pending disconnect.
- **Behaviour:** stop no longer returns before the socket is down unless the host shutdown token is cancelled first (then it throws `OperationCanceledException`), and a fault from `DisconnectAsync()` propagates out of `StopAsync` and out of `DisposeAsync` (the lifetime token is already cancelled and the handler cleared, so no further message reaches the handler).
- **Tests:** `StopAsync_Disconnects_Socket_Mode` asserts `DisconnectAsync`; `StopAsync_Waits_For_Socket_Mode_Disconnect_To_Finish`, `StopAsync_When_Socket_Mode_Disconnect_Fails_Surfaces_The_Fault_And_Stops_Handling`, `StopAsync_When_Socket_Mode_Disconnect_Never_Completes_Returns_On_Cancellation` and `DisposeAsync_When_Socket_Mode_Disconnect_Fails_Still_Disposes_The_Socket_Client` are new, in `tests/Scout.Adapter.Slack.Tests/SlackChatAdapterTests.cs`.
- **Not changed:** `ConversationWorker` still stops adapters in sequence without per-adapter fault isolation, so a Slack disconnect fault stops the adapters after it from being stopped; tracked in [[pages/roadmap/followups]].
- **Invariants:** [[pages/invariants/pluggable-boundaries]] holds; SlackNet types stay inside the adapter project and its tests.

## Version notes: `pages/services/adapter-slack.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- A start whose auth.test or Socket Mode connect fails stops the adapter before it throws, and a stop that lands during the start wins: the start checks the lifecycle under a connect gate the stop also takes, so the socket is never left open after a stop, and the connect runs on the lifetime token, so a stop cancels a pending connect instead of waiting on its own token (2026-11-11). Earlier: A message reads the running state and the lifetime token in one step under the lifecycle's lock, so one a stop overtakes returns without work instead of throwing outside the adapter, proved by a test through an internal hook (2026-11-10). A test of a failed reply over the limit through the real pump: its first part, one truncation marker, the footer (2026-11-10). A terminal call before a successful Begin throws instead of reporting success with nothing posted (2026-11-09). Earlier: SlackNet 0.20.0 (2026-11-08); the adapter builds and its tests pass unchanged
