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

## 2026-10-28 - Threads, long replies, typing, reply reference, voice messages

- **Asked for:** "improvements in the Discord chat" and "use audio to communicate - me recording".
- **What changed:** a mention in a server text channel opens a public thread on the user's message and the reply streams there (`ReplyInThreads`, default true; falls back to the channel when Discord refuses); a reply that stays in the channel is a Discord reply to the user's message; the typing indicator shows from Begin to the first streamed content; a reply longer than one embed continues in up to five messages, split at paragraphs, lines or spaces and never inside a code fence that fits a part, where before it was cut at 4,096 characters; voice messages reach the core as audio with a download delegate ([[pages/decisions/0024-voice-messages-transcribed-locally]]). The sink stopped appending its own cursor after the pump's, which showed two cursors while streaming.
- **Tests:** `DiscordReplySplitterTests`, `DiscordResponseSinkPartsTests`, `DiscordChatAdapterThreadTests`, `DiscordVoiceMessageTests`, and a typing-at-stop test in `DiscordChatAdapterDispatchTests`. `Capabilities_Are_FineGrained_4096` and `Complete_OverLimit_Body_Preserves_Head_Not_Just_Footer` were replaced, because the sink no longer caps a reply at one embed.

## 2026-10-22 - The gateway handler stops awaiting the conversation

- **Symptom:** while a reply streamed, the owner's log showed `[Discord] Gateway: A MessageReceived handler is blocking the gateway task.` Discord.Net 3.20.2 raises that warning in `DiscordSocketClient.TimeoutWrap` when an event handler outlives the configured handler timeout, and then keeps awaiting the handler to completion inside the gateway event dispatch.
- **Cause:** `OnMessageReceivedAsync` awaited the whole conversation (`handler(chatMessage, sink, lifetimeToken)`) inside the gateway event.
- **What changed:** the handler still projects, filters and logs the message exactly as before, then starts the conversation with `Task.Run` and returns. The started work keeps the old catch blocks: a cancellation on the lifetime token logs at debug, any other exception logs an error. Each conversation is tracked in a `ConcurrentDictionary` until it completes. `StopAsync` cancels the lifetime token, unsubscribes, waits for the tracked conversations bounded by 10 s and the caller's token (a timeout logs a warning), then stops and logs out the client. The filter-and-dispatch step is the internal `OnInboundAsync` seam, and `StartAsync` arms the adapter through the internal `Arm`, so tests drive both without a Discord login.
- **Behaviour:** two messages can now be in flight at once; conversation memory is already concurrency-safe, and their turns may interleave in history. Per-channel reply ordering is not guaranteed.
- **Tests:** `The_Gateway_Handler_Returns_Before_The_Conversation_Finishes`, `A_Failing_Conversation_Is_Logged_Never_Unobserved` and `Stop_Waits_For_In_Flight_Conversations` in `tests/Scout.Adapter.Discord.Tests/DiscordChatAdapterDispatchTests.cs`; the translator and projection tests are unchanged and green.
- **Not changed:** the Slack and Teams adapters. SlackNet acknowledges a legacy event handler before calling it and fans socket envelopes out through Rx `SelectMany`, so a long Slack conversation neither blocks the socket nor delays the ack. The Teams turn holds the inbound webhook request for the whole conversation, which matters only once a webhook host is wired.
- **Invariants:** [[pages/invariants/no-executive-writes]] holds (the sink is still bound to the originating channel); [[pages/invariants/pluggable-boundaries]] holds (no Discord.Net type leaves the adapter).
