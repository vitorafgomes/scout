---
title: "Conversation: Hot spots"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Conversation service page."
page_type: service
status: active
description: "Read when a change touches: Hot spots."
source: ["src/Scout.Conversation/", "tests/Scout.Conversation.Tests/"]
reliability: high
---

## Hot spots

- `src/Scout.Conversation/Streaming/StreamPump.cs:69`: edit throttling, timeout and truncation for every platform; `:237` and `:296`: the bounded terminal write and the capped body of a reply that ends on an error.
- `src/Scout.Conversation/ConversationServiceCollectionExtensions.cs:49`: `NoOpSkillSelector` is a `TryAdd` fallback, so `AddScoutSkills` must run first (`src/Scout.Host.Console/HostComposition.cs:59`).
