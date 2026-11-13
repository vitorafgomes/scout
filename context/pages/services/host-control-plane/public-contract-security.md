---
title: "Host.ControlPlane: Public contract: Security"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Host.ControlPlane service page."
page_type: service
status: active
description: "Read when a change touches: Security; Configuration and environment; External boundaries."
source: ["src/Scout.Host.ControlPlane/", "tests/Scout.ControlPlane.Tests/", "ui/packages/main/src/control-plane/"]
reliability: high
---

## Public contract

- **Security:** two callers. The desktop's `SessionToken` (`src/Scout.Host.ControlPlane/Security/SessionToken.cs:11`) is read from the environment or minted at startup; the worker's token is read from the environment only. `CallerTokens.Create` (`src/Scout.Host.ControlPlane/Security/CallerTokens.cs:32`) pairs them: a missing or blank worker token leaves the worker role empty and logs one warning, "SCOUT_CONTROLPLANE_WORKER_TOKEN is not set; the WorkerGate service refuses every caller, so the worker cannot request a write." (`:39`), and a worker token equal to the desktop token throws, without naming either value (`:47`). `Program` resolves the pair before Kestrel binds, so equal tokens refuse to start and the warning is logged once at startup (`src/Scout.Host.ControlPlane/Program.cs:27`). `TokenAuthInterceptor` (`src/Scout.Host.ControlPlane/Security/TokenAuthInterceptor.cs:17`) accepts a token in an `authorization` bearer header or `x-scout-token`, compares it in constant time against the desktop token and then the worker token (`src/Scout.Host.ControlPlane/Security/SessionToken.cs:62`), and answers `UNAUTHENTICATED` when it matches neither (`src/Scout.Host.ControlPlane/Security/TokenAuthInterceptor.cs:88`). Then it allows only the role's services, by the method's `/<package>.<Service>/` prefix taken from the generated service descriptors (`:100`): the desktop token reaches `Features`, `Providers`, `Models` and `Operator` (`:23`), the worker token reaches `WorkerGate` (`:31`), and everything else, including a method of a service added later, answers `PERMISSION_DENIED` "This caller may not call <method>." (`:95`). Neither token is ever logged or put in an exception message.

- **Configuration and environment:** `Scout:ControlPlane:SocketPath` (`src/Scout.Host.ControlPlane/Program.cs:20`); environment variables `SCOUT_CONTROLPLANE_TOKEN` (`src/Scout.Host.ControlPlane/Security/SessionToken.cs:14`) and `SCOUT_CONTROLPLANE_WORKER_TOKEN` (`src/Scout.Host.ControlPlane/Security/CallerTokens.cs:13`), both scrubbed from the Ollama daemon's environment ([[pages/services/control-plane]]); `XDG_RUNTIME_DIR` for the socket directory (`src/Scout.Host.ControlPlane/Transport/ControlPlaneEndpoint.cs:28`); the domain keys listed on [[pages/services/control-plane]].

- **External boundaries:** Unix domain socket `controlplane.sock` on Linux and macOS, named pipe `scout-controlplane` on Windows (`src/Scout.Host.ControlPlane/Transport/ControlPlaneEndpoint.cs:12,15`); filesystem permissions on the socket and its directory (`:41`, `:99`).
