---
title: "Adapter.Discord - history"
version: "1.0"
date: 2026-10-22
changes: "Initial history note: the gateway handler no longer awaits the conversation"
page_type: reference
status: artefact
description: "What changed in the Discord adapter and when."
source:
  - chat
  - src/Scout.Adapter.Discord/DiscordChatAdapter.cs
  - tests/Scout.Adapter.Discord.Tests/DiscordChatAdapterDispatchTests.cs
reliability: high
---

# Adapter.Discord - history

> Dated changes to [[pages/services/adapter-discord]]; the service page states how it works now.

## 2026-10-22 - The gateway handler stops awaiting the conversation

- **Symptom:** while a reply streamed, the owner's log showed `[Discord] Gateway: A MessageReceived handler is blocking the gateway task.` Discord.Net 3.20.2 raises that warning in `DiscordSocketClient.TimeoutWrap` when an event handler outlives the configured handler timeout, and then keeps awaiting the handler to completion inside the gateway event dispatch.
- **Cause:** `OnMessageReceivedAsync` awaited the whole conversation (`handler(chatMessage, sink, lifetimeToken)`) inside the gateway event.
- **What changed:** the handler still projects, filters and logs the message exactly as before, then starts the conversation with `Task.Run` and returns. The started work keeps the old catch blocks: a cancellation on the lifetime token logs at debug, any other exception logs an error. Each conversation is tracked in a `ConcurrentDictionary` until it completes. `StopAsync` cancels the lifetime token, unsubscribes, waits for the tracked conversations bounded by 10 s and the caller's token (a timeout logs a warning), then stops and logs out the client. The filter-and-dispatch step is the internal `OnInboundAsync` seam, and `StartAsync` arms the adapter through the internal `Arm`, so tests drive both without a Discord login.
- **Behaviour:** two messages can now be in flight at once; conversation memory is already concurrency-safe, and their turns may interleave in history. Per-channel reply ordering is not guaranteed.
- **Tests:** `The_Gateway_Handler_Returns_Before_The_Conversation_Finishes`, `A_Failing_Conversation_Is_Logged_Never_Unobserved` and `Stop_Waits_For_In_Flight_Conversations` in `tests/Scout.Adapter.Discord.Tests/DiscordChatAdapterDispatchTests.cs`; the translator and projection tests are unchanged and green.
- **Not changed:** the Slack and Teams adapters. SlackNet acknowledges a legacy event handler before calling it and fans socket envelopes out through Rx `SelectMany`, so a long Slack conversation neither blocks the socket nor delays the ack. The Teams turn holds the inbound webhook request for the whole conversation, which matters only once a webhook host is wired.
- **Invariants:** [[pages/invariants/no-executive-writes]] holds (the sink is still bound to the originating channel); [[pages/invariants/pluggable-boundaries]] holds (no Discord.Net type leaves the adapter).
