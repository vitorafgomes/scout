---
title: "ControlPlane.Contracts: How it works"
version: "1.0"
date: 2026-11-13
changes: "Split out of the ControlPlane.Contracts service page."
page_type: service
status: active
description: "Read when a change touches: How it works."
source: ["src/Scout.ControlPlane.Contracts/", "tests/Scout.ControlPlane.Tests/"]
reliability: high
---

## How it works

```mermaid
flowchart LR
    p["protos/*.proto"] --> gen["Grpc.Tools codegen"]
    gen --> srv["server stubs (Host.ControlPlane)"]
    gen --> cli["client stubs (tests, Host.Console's WorkerGate client)"]
```
