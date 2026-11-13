---
title: "Host.ControlPlane: Hot spots"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Host.ControlPlane service page."
page_type: service
status: active
description: "Read when a change touches: Hot spots."
source: ["src/Scout.Host.ControlPlane/", "tests/Scout.ControlPlane.Tests/", "ui/packages/main/src/control-plane/"]
reliability: high
---

## Hot spots

- `src/Scout.Host.ControlPlane/Transport/NativeUnix.cs:15`: native `stat` interop for socket ownership checks.
- `src/Scout.Host.ControlPlane/Transport/ControlPlaneEndpoint.cs:54`: pre-flight on an existing socket path.
- `src/Scout.Host.ControlPlane/Security/TokenAuthInterceptor.cs:35`: the interceptor covers unary and streaming handlers; a new RPC kind must be added here, and a new service is denied to both callers until it joins a role list (`:23`, `:31`).
