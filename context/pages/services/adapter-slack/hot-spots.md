---
title: "Adapter.Slack: Hot spots"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Adapter.Slack service page."
page_type: service
status: active
description: "Read when a change touches: Hot spots."
source: ["src/Scout.Adapter.Slack/", "tests/Scout.Adapter.Slack.Tests/"]
reliability: high
---

## Hot spots

- `src/Scout.Adapter.Slack/SlackChatAdapter.cs:243`: projection of the SlackNet event into `InboundSlackMessage`.
- `src/Scout.Adapter.Slack/Streaming/SlackResponseSink.cs:265`: truncation budget with cursor glyph and citation footer.
