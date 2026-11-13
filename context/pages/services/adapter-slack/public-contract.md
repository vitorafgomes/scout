---
title: "Adapter.Slack: Public contract"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Adapter.Slack service page."
page_type: service
status: active
description: "Read when a change touches: Public contract."
source: ["src/Scout.Adapter.Slack/", "tests/Scout.Adapter.Slack.Tests/"]
reliability: high
---

## Public contract

- **Entry point:** `AddSlackAdapter(IServiceCollection, IConfiguration)` at `src/Scout.Adapter.Slack/SlackServiceCollectionExtensions.cs:20`; registers nothing unless both tokens are set (`:32`).
- **Key types:** `SlackChatAdapter : IChatAdapter` (`src/Scout.Adapter.Slack/SlackChatAdapter.cs:18`), `SlackMessageTranslator` (`src/Scout.Adapter.Slack/SlackMessageTranslator.cs:13`), `SlackResponseSink : IResponseSink` (`src/Scout.Adapter.Slack/Streaming/SlackResponseSink.cs:34`), `SlackMessageHandle` (`src/Scout.Adapter.Slack/Streaming/SlackMessageHandle.cs:10`), `SlackOptions` (`src/Scout.Adapter.Slack/SlackOptions.cs:8`), `SlackOriginPoster : IOriginPoster` (`src/Scout.Adapter.Slack/SlackOriginPoster.cs:12`), registered with the adapter (`src/Scout.Adapter.Slack/SlackServiceCollectionExtensions.cs:41`).
- **Configuration keys:** `Scout:Adapters:Slack` with `BotToken`, `AppLevelToken` (`src/Scout.Adapter.Slack/SlackServiceCollectionExtensions.cs:18`).
- **External boundaries:** SlackNet service builder and Socket Mode client (`src/Scout.Adapter.Slack/SlackChatAdapter.cs:64`, `:126`), on SlackNet 0.20.0 (since 2026-11-08, [[pages/history/adapter-slack]]); `StopAsync` awaits `DisconnectAsync`, bounded by the caller's cancellation token (`:166`), and lets its fault propagate; `DisposeAsync` disposes the lifecycle and awaits the socket client's `DisposeAsync` even when stop throws (`:174`); writes are `Chat.PostMessage` (`src/Scout.Adapter.Slack/Streaming/SlackResponseSink.cs:84`) and `Chat.Update` (`:96`). Body capped at 3000 characters (`:38`). A later plain-text `Chat.PostMessage` into the originating thread (`src/Scout.Adapter.Slack/SlackOriginPoster.cs:43`).
