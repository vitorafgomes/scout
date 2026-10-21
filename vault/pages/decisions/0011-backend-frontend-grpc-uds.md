---
title: "Backend-frontend transport - gRPC over Unix domain socket / named pipe"
version: "1.0"
date: 2026-05-28
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: adr
status: accepted
description: "Scout's desktop app is Electron (Node main process + Angular renderer). The .NET backend runs as a supervised child process (sidecar)."
source:
  - "obsidian-vault/scout/06-decisions/2026-05-28-backend-frontend-grpc-uds.md"
reliability: medium
---

# Backend-frontend transport - gRPC over Unix domain socket / named pipe

## Context

Scout's desktop app is Electron (Node main process + Angular renderer). The
.NET backend runs as a supervised child process (sidecar). The two must talk:
the UI is a control panel (provider lifecycle, features, model management,
vault), and several flows are inherently streaming (Ollama/Whisper model-pull
progress, worker/provider status events).

The pre-restructure design used:

- **Loopback TCP HTTP** (Kestrel bound to `127.0.0.1:<port>`) with a
  `LoopbackOnlyMiddleware` defence-in-depth filter.
- **SSE** (`text/event-stream`) for streaming pull progress.
- A hand-written `ipc-contract.ts` duplicating request/response shapes that
  also exist in C#.

Weaknesses we are deliberately leaving behind:

- **A loopback TCP port is reachable by any process running as the same user.**
  The middleware is a band-aid: it cannot stop another local process from
  connecting to the port. This is the main security weakness.
- **Contract drift:** TS and C# shapes are maintained by hand in two places.
- **SSE** is text-concatenation streaming, one-directional and fragile.

The restructure mandates streaming-first (see feedback-scout-streaming-first (private note))
and the user asked explicitly for a more performant and more secure transport,
not a port of the old one.

## Decision

Backend-frontend transport is **gRPC over a Unix domain socket (Linux/macOS) or
a named pipe (Windows)** — no TCP port. Topology:

```
Angular renderer ──contextBridge IPC──▶ Electron main ──gRPC over UDS/pipe──▶ .NET backend
 (sandbox, contextIsolation,            (sole gRPC client,                   (no TCP port)
  no nodeIntegration)                    broker to renderer)
```

- **Transport:** Kestrel binds a UDS / named pipe, not a port. The socket file
  lives in the user's runtime directory with `0600` permissions; the Windows
  named pipe is ACL'd to the current user.
- **Auth handshake:** the backend mints a per-session token at spawn and hands
  it to the Electron main process out-of-band (inherited fd / env), never on
  disk in a world-readable place. Main sends it as gRPC metadata on every call.
  Defence-in-depth even if socket permissions are misconfigured.
- **Contracts:** `.proto` files are the single source of truth, with codegen for
  both C# (`Grpc.AspNetCore`) and TypeScript. Eliminates the hand-maintained
  `ipc-contract.ts` drift.
- **Streaming:** streaming flows (model-pull/download progress, provider/worker
  status/events) are gRPC **server-streaming** RPCs. Bidirectional streaming is
  available if a future flow needs it.
- **Renderer boundary:** the renderer never touches the socket. The Electron
  main process is the only gRPC client and re-exposes a minimal, allow-listed
  surface via `contextBridge` with `contextIsolation`, `sandbox`, and no
  `nodeIntegration`. High-volume streams reach the renderer over a
  `MessagePort` (`MessageChannelMain`) rather than `ipcRenderer` event spam, for
  backpressure and throughput.

## Alternatives considered

### Loopback TCP HTTP + SSE (the old design)

Rejected. The open local port is the security weakness this decision exists to
remove, SSE is a weaker streaming primitive than gRPC server-streaming, and the
hand-written TS contract drifts from C#.

### stdio JSON-RPC (Electron spawns backend, talks over stdin/stdout)

The most secure and lowest-dependency option: no socket and no port, just the
private parent-child pipe, unreachable by any other process. Rejected as the
primary choice because the streaming protocol (framing, multiplexing,
backpressure) must be hand-rolled, and binary/high-volume payloads are awkward.
It remains the fallback if the gRPC/UDS dependency surface proves too heavy.

### WebSocket over UDS

Full-duplex and streaming-friendly, but loses REST/contract ergonomics and
requires a bespoke message-envelope protocol. gRPC gives typed contracts +
streaming without the hand-rolled envelope.

## Consequences

### Positive

- **No network-reachable surface.** The whole "other local process connects to
  the port" class of attack disappears; filesystem permissions + a session
  token gate access.
- **Performance.** UDS avoids the TCP/IP stack; HTTP/2 multiplexing + protobuf
  binary framing beat loopback HTTP/JSON + SSE.
- **One contract.** `.proto` codegen kills TS/C# drift.
- **Streaming is native**, matching the streaming-first mandate, rather than an
  SSE remedy.

### Negative

- **gRPC dependency + codegen** on both sides (`Grpc.AspNetCore` in .NET, a gRPC
  / proto toolchain in the Electron build). More moving parts than raw HTTP.
  Mitigation: codegen is wired into the build; the proto surface is small
  (control-panel commands + a few streams).
- **UDS on Windows** needs the named-pipe path; the transport abstraction must
  branch per-OS at bind time. Kestrel supports both, but it is two code paths.
- **gRPC-over-UDS from Node** requires a gRPC client that targets a UDS/pipe
  address; verify the chosen Node gRPC library supports it at implementation
  time (fallback: a thin local HTTP/2-over-UDS shim, or stdio-JSON-RPC).

### Operational

- Per Scout CLAUDE.md quality gates, the PR introducing this transport runs
  `security-auditor` (new IPC boundary + token handling) and `architect-reviewer`
  (cross-process contract surface).
- Production-readiness: the socket/pipe is an external boundary — at least one
  failure-injection test (backend not yet listening, socket disappears
  mid-stream, token rejected) per `playbooks/failure-testing.md`.

## Status / sequencing

Decision accepted. The **.NET server side** — gRPC-over-UDS/named-pipe host
(`Scout.Host.ControlPlane`), `.proto` contracts (`Scout.ControlPlane.Contracts`),
ported control-plane domain services (`Scout.ControlPlane`), and the per-session
token interceptor — is **implemented and tested** on `feat/scout-llm-gateway`
alongside the conversation core and the Discord/Slack/Teams adapters (security
audit + code review passed; findings fixed). The **Node/Electron gRPC client,
the Windows named-pipe spike, the TypeScript codegen, MessagePort streaming, and
the `ui/` rewiring remain a follow-on phase**.

## References

- Streaming-first mandate: feedback-scout-streaming-first (private note).
- LLM transport decision: [[pages/decisions/0012-llm-communication-through-semantic-kernel]].
- Replaces the loopback-HTTP + SSE + hand-written `ipc-contract.ts` approach
  from the archived `Scout.Host.Worker` (frozen under `src/old/`).
