---
title: "Adapter.Local: Hot spots"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Adapter.Local service page."
page_type: service
status: active
description: "Read when a change touches: Hot spots."
source: ["src/Scout.Adapter.Local/", "tests/Scout.Adapter.Local.Tests/", "src/Scout.Host.Console/HostComposition.cs"]
reliability: high
---

## Hot spots

- `src/Scout.Adapter.Local/LocalChatAdapter.cs:451`: `ToMessage`, the only place `IsLocalOwner` is set to true. Trust comes from holding the worker's stdin ([[pages/decisions/0028-local-quick-ask-assistant]]).
- `src/Scout.Adapter.Local/LocalEventWriter.cs:23`: one `WriteLine` per event is what keeps stdout lines whole.
