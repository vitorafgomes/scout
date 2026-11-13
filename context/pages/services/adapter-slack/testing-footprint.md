---
title: "Adapter.Slack: Testing footprint"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Adapter.Slack service page."
page_type: service
status: active
description: "Read when a change touches: Testing footprint."
source: ["src/Scout.Adapter.Slack/", "tests/Scout.Adapter.Slack.Tests/"]
reliability: high
---

## Testing footprint

`tests/Scout.Adapter.Slack.Tests` (77 test attributes, 86 test cases, 9 files on 2026-11-11): the lifetime token handed out only while running, a read before a start or after a stop answering false without a throw (`tests/Scout.Adapter.Slack.Tests/SlackAdapterLifecycleTests.cs:68`); through the adapter, a message while running reaches the handler of that start and one after stop reaches none and never throws (`tests/Scout.Adapter.Slack.Tests/SlackChatAdapterTests.cs:220`), and a start whose `auth.test` fails surfaces the fault, never connects, hands no message to the handler and still stops, the Rule I test of the start (`:238`); a start whose `auth.test` or connect fails leaves the adapter stopped, its handler never reached (`:331`), and a stop that lands while `auth.test` or the connect is pending wins, no Socket Mode connection left open after it (`:355`), and a stop that lands while the connect is still pending, the connect waiting on the token it was given, returns promptly instead of waiting on its own token, the start returning and the adapter stopped (`:406`); a message a stop overtakes at its session read, the adapter stopped from `BeforeSessionRead`, an internal hook at the start of `TryGetLifetimeToken`, null outside tests (`src/Scout.Adapter.Slack/SlackAdapterLifecycle.cs:42`, `:119`), is dropped without a throw and never reaches the handler (`tests/Scout.Adapter.Slack.Tests/SlackChatAdapterTests.cs:258`); the lock against a stop on another thread during the read, and the start's order of publication, are proved by reading the lock, not by a test; a failed reply over the limit, driven through the real pump, shows its first part, one truncation marker and the footer (`tests/Scout.Adapter.Slack.Tests/Streaming/SlackSinkBoundaryTests.cs:263`), the origin reference for a top-level and a threaded message, and the later post (`SlackOriginPosterTests.cs`: plain text into the channel and thread, broadcast mentions posted as plain text, the 3000 cap, a `SlackException` false with a Warning naming no channel, an adapter not started, unreadable payloads, the composed notifier reaching the Slack poster); translator rules, inbound projection, adapter and lifecycle (stop awaits the Socket Mode disconnect, returns on cancellation if it never completes, and surfaces its fault; dispose still disposes the socket client when stop faults), DI gating including a missing app-level token, sink behaviour (a terminal call before Begin, or after a Begin Slack refused, throws and writes nothing) and a sink boundary test. No live Slack connection.
