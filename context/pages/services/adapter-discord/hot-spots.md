---
title: "Adapter.Discord: Hot spots"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Adapter.Discord service page."
page_type: service
status: active
description: "Read when a change touches: Hot spots."
source: ["src/Scout.Adapter.Discord/", "tests/Scout.Adapter.Discord.Tests/"]
reliability: high
---

## Hot spots

- `src/Scout.Adapter.Discord/DiscordChatAdapter.cs:198`: inbound gateway handler; `:232` is the filter-and-dispatch seam the tests drive; `:269` opens the thread and builds the sink.
- `src/Scout.Adapter.Discord/Streaming/DiscordResponseSink.cs:237`: the part render loop; `src/Scout.Adapter.Discord/Streaming/DiscordReplySplitter.cs:20` decides where a reply splits.
