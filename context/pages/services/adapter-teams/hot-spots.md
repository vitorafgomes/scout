---
title: "Adapter.Teams: Hot spots"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Adapter.Teams service page."
page_type: service
status: active
description: "Read when a change touches: Hot spots."
source: ["src/Scout.Adapter.Teams/", "tests/Scout.Adapter.Teams.Tests/"]
reliability: high
---

## Hot spots

- `src/Scout.Adapter.Teams/Streaming/TeamsResponseSink.cs:140`: streaming update with transport-error mapping (`src/Scout.Adapter.Teams/Streaming/TeamsErrorMapper.cs:15`).
- `src/Scout.Adapter.Teams/TeamsServiceCollectionExtensions.cs:18`: the missing webhook, so a configured Teams adapter receives nothing in the shipped host.
