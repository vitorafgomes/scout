---
title: "Adapter.Slack: Desktop setup"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Adapter.Slack service page."
page_type: service
status: active
description: "Read when a change touches: Desktop setup."
source: ["src/Scout.Adapter.Slack/", "tests/Scout.Adapter.Slack.Tests/"]
reliability: high
---

## Desktop setup

The desktop app creates the Slack app from a manifest and passes the two tokens as `SCOUT__ADAPTERS__SLACK__BOTTOKEN` and `SCOUT__ADAPTERS__SLACK__APPLEVELTOKEN` ([[pages/decisions/0023-slack-setup-from-a-manifest]], [[pages/services/desktop-ui]]). The manifest (`ui/packages/main/src/slack/slack-setup.ts:19`) turns Socket Mode on and asks for the events this adapter reads, `message.channels`, `message.groups` and `message.im`, with `channels:history`, `groups:history`, `im:history` and `chat:write`. A change here that reads another event or calls another Web API method changes that manifest too; otherwise an app created from it never receives the event or lacks the scope.
