---
title: "Adapter.Teams: How it works"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Adapter.Teams service page."
page_type: service
status: active
description: "Read when a change touches: How it works."
source: ["src/Scout.Adapter.Teams/", "tests/Scout.Adapter.Teams.Tests/"]
reliability: high
---

## How it works

```mermaid
flowchart LR
    host["host webhook (not in repo)"] -.-> pa["TeamsChatAdapter.ProcessActivityAsync"]
    pa --> tr["TeamsMessageTranslator"]
    tr --> hd["handler (ConversationConductor)"]
    hd --> sk["TeamsResponseSink"]
    sk --> snd["ITeamsActivitySender"]
```

`StartAsync` hands its handler to `TeamsAdapterLifecycle.Start` (`src/Scout.Adapter.Teams/TeamsChatAdapter.cs:77`), which stores it with the lifetime token and the running state under one lock (`src/Scout.Adapter.Teams/TeamsAdapterLifecycle.cs:76`, `:88`); a turn reads the running state, the handler and the token in one step, `TryGetSession` (`:42`, `src/Scout.Adapter.Teams/TeamsChatAdapter.cs:106`), so a turn that sees the adapter running is always dispatched to the handler of that start, and a turn arriving before a start or once a stop has begun returns without work. `StopAsync` of a running adapter clears the running state and the handler in that same lock before it waits for the lifetime token's cancellation (`src/Scout.Adapter.Teams/TeamsAdapterLifecycle.cs:98`, `:109`), so a `StartAsync` that runs while that stop unwinds keeps its handler, and however two stops and a start interleave, an adapter left running has its handler. The adapter holds no handler of its own. Each turn links the request token to the lifetime token so host shutdown cancels it (`src/Scout.Adapter.Teams/TeamsChatAdapter.cs:113`). The activity is projected inside the turn's try (`:121`), so an activity the SDK cannot read, such as a mention entity whose `mentioned` is not an account, where `Activity.GetMentions` throws a `JsonSerializationException`, is logged at Error with the activity id and never escapes into the webhook host (`:137`).

- **Addressed to the bot.** A 1:1 chat always is. In a group chat or channel the translator accepts a message only when one of its mention entities carries the bot's account id, projected from the activity as `InboundTeamsMessage.MentionedIds` (`src/Scout.Adapter.Teams/TeamsMessageTranslator.cs:140`), or when the text holds `<at>` and the bot's display name (`src/Scout.Adapter.Teams/TeamsMessageTranslator.cs:80`); with neither, a mention of someone else never counts as the bot's, even when the activity carries no recipient name. The text handed to the core carries no tag of the bot: the translator removes the tag text of every mention entity whose account is the bot's, projected as `InboundTeamsMessage.RecipientMentionTexts` (`:141`), so a mention matched by id is stripped even when its tag differs from the bot's display name, then the tag built from the display name, or every `at` tag when the activity carries no recipient name (`:95`); mentions of other users stay.
- **Streaming.** Begin sends the informative typing activity and keeps its resource id as the stream id every later activity echoes. A Begin answered without an id (null or empty, which `ITeamsActivitySender` allows) is treated like a refused stream: a Warning, no interim update on the wire, and a plain final message without a `streaminfo` entity (`src/Scout.Adapter.Teams/Streaming/TeamsResponseSink.cs:132`). A Begin that fails otherwise (a 5xx, a reset) escapes to the caller; a 403 `ContentStreamNotAllowed` downgrades to final-only. A sink built with streaming off, for a group chat or a channel, declares `FineGrainedEdit` false (`src/Scout.Adapter.Teams/Streaming/TeamsResponseSink.cs:91`), so the pump buffers the reply and sends one final instead of scheduling interim renders the sink would drop ([[pages/decisions/0013-conversation-core-streaming-sink]], since 2026-11-11; until then the capability was true whatever the mode). The two downgrades above happen after the pump has read the capabilities, so their interim renders are still sent and dropped by the sink.
