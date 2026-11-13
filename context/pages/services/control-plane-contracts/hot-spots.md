---
title: "ControlPlane.Contracts: Hot spots"
version: "1.0"
date: 2026-11-13
changes: "Split out of the ControlPlane.Contracts service page."
page_type: service
status: active
description: "Read when a change touches: Hot spots."
source: ["src/Scout.ControlPlane.Contracts/", "tests/Scout.ControlPlane.Tests/"]
reliability: high
---

## Hot spots

- `src/Scout.ControlPlane.Contracts/protos/models.proto:13`: `DeleteModel` is the RPC that removes model files from the host.
