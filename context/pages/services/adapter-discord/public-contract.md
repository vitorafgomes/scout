---
title: "Adapter.Discord: Public contract"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Adapter.Discord service page."
page_type: service
status: active
description: "Read when a change touches: Public contract."
source: ["src/Scout.Adapter.Discord/", "tests/Scout.Adapter.Discord.Tests/"]
reliability: high
---

## Public contract

- **Entry point:** `AddDiscordAdapter(IServiceCollection, IConfiguration)` at `src/Scout.Adapter.Discord/DiscordServiceCollectionExtensions.cs:22`; a blank token registers nothing (`:32`).
- **Key types:** `DiscordChatAdapter : IChatAdapter` (`src/Scout.Adapter.Discord/DiscordChatAdapter.cs:24`), `DiscordMessageTranslator` (`src/Scout.Adapter.Discord/DiscordMessageTranslator.cs:20`), `DiscordResponseSink : IResponseSink, IDisposable` (`src/Scout.Adapter.Discord/Streaming/DiscordResponseSink.cs:30`), internal `DiscordReplySplitter` (`src/Scout.Adapter.Discord/Streaming/DiscordReplySplitter.cs:10`), `DiscordOptions` (`src/Scout.Adapter.Discord/DiscordOptions.cs:6`), `InboundDiscordMessage` and `InboundVoiceMessage` (`src/Scout.Adapter.Discord/InboundDiscordMessage.cs`), internal `DiscordAttachmentDownloader` (`src/Scout.Adapter.Discord/DiscordAttachmentDownloader.cs:7`), internal `DiscordAdapterLifecycle` (`src/Scout.Adapter.Discord/DiscordAdapterLifecycle.cs:11`), `DiscordOriginPoster : IOriginPoster` (`src/Scout.Adapter.Discord/DiscordOriginPoster.cs:13`), registered with the adapter (`src/Scout.Adapter.Discord/DiscordServiceCollectionExtensions.cs:42`).
- **Configuration keys:** `Scout:Adapters:Discord` with `BotToken` (`src/Scout.Adapter.Discord/DiscordServiceCollectionExtensions.cs:18`) and `ReplyInThreads`, default true (`src/Scout.Adapter.Discord/DiscordOptions.cs:19`).
- **External boundaries:** Discord.Net socket client with intents Guilds, GuildMessages, DirectMessages and MessageContent (`src/Scout.Adapter.Discord/DiscordChatAdapter.cs:71`). Writes to Discord: `CreateThreadAsync` on the user's message (`src/Scout.Adapter.Discord/DiscordChatAdapter.cs:324`), `SendMessageAsync` (`src/Scout.Adapter.Discord/Streaming/DiscordResponseSink.cs:74`), `ModifyAsync` on its own messages (`:83`) and the typing state (`:85`); a later plain-text `SendMessageAsync` into the originating conversation (`src/Scout.Adapter.Discord/DiscordOriginPoster.cs:65`). Read: one HTTPS GET of a voice message attachment from the URL Discord put on the message, through one shared client whose pooled connections are recycled every 2 minutes, 30 s to the response headers, non-success status throws (`src/Scout.Adapter.Discord/DiscordAttachmentDownloader.cs:15`); the conductor bounds the whole download, body included, at 30 s.
- **Bot permissions this needs in a server:** View Channel (1<<10), Send Messages (1<<11), Embed Links (1<<14), Read Message History (1<<16, for the reply reference), Create Public Threads (1<<35) and Send Messages in Threads (1<<38). Without Create Public Threads the reply falls back to the channel.
