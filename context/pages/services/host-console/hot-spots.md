---
title: "Host.Console: Hot spots"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Host.Console service page."
page_type: service
status: active
description: "Read when a change touches: Hot spots."
source: ["src/Scout.Host.Console/", "tests/Scout.Host.Console.Tests/"]
reliability: high
---

## Hot spots

- `src/Scout.Host.Console/HostComposition.cs:59`: registration order matters; skills must precede conversation.
- `src/Scout.Host.Console/HostComposition.cs:90`: the operator is composed after the write gate and the YouTube plugin, because it needs the gate's `GrpcWriteGate` and checks for the YouTube client.
- `src/Scout.Host.Console/ConversationWorker.cs:45`: adapters start sequentially, one failure aborts the host.
