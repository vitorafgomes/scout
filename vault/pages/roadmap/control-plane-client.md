---
title: "Control plane client"
version: "1.0"
updated: 2026-10-22
date: 2026-10-22
changes: "Initial initiative page"
page_type: roadmap
status: active
description: "Give the desktop UI a gRPC client to Scout.Host.ControlPlane so the Providers, Features and Models screens and the Ollama pull run through the .NET control plane instead of Node."
source:
  - chat
  - ui/packages/main/src
  - src/Scout.ControlPlane.Contracts
reliability: medium
---

# Control plane client

> When this is done, every host mutation the desktop app performs (model pulls, provider start and stop, feature credentials) goes through [[pages/services/host-control-plane]], and the Providers and Features screens work again.

## Why now

The owner decided on 2026-10-22 to keep the gRPC control plane (ADR [[pages/decisions/0011-backend-frontend-grpc-uds]]) rather than delete it. The .NET side (contracts, domain, host, 65 tests) has been complete since the restructure, but [[pages/services/desktop-ui]] still talks to a `SCOUT_HTTP=` endpoint that only the retired worker printed, so two screens are dead on every run. The project cannot be called finished with a tested server that nothing calls.

## Scope

- **In scope:** `@grpc/grpc-js` client in `ui/packages/main` over `unix:` on Linux and macOS; a Windows named-pipe spike; `ts-proto` codegen from `src/Scout.ControlPlane.Contracts/*.proto` replacing the hand-written shapes in `ui/packages/shared/src/ipc-contract.ts`; launching `Scout.Host.ControlPlane` beside the console worker with the per-session token; moving the providers, features, llm and whisper IPC handlers onto the client.
- **Out of scope:** new control-plane features on the .NET side; Slack and Teams onboarding; any change to the chat path.

## Target outcome

Providers and Features screens render live data from the control plane on Linux and macOS, with one failing-call test per gRPC method (Rule I) and the three-OS package step green.

## Status

- **As of 2026-10-22:** phase 1 done on Linux: the desktop main process launches the real host and round-trips `ListProviders` over gRPC/UDS in `bun test`. The macOS and Windows legs of `ui-ci.yml` have not run it yet. Detail on the Consumers section of [[pages/services/host-control-plane]].
- **Next milestone:** green macOS and Windows CI legs, then phase 2 (codegen and packaged protos).
- **Blocker:** none.

## Workstreams / phases

### Phase 1 - Transport spike
- **Status:** done on Linux; macOS and Windows legs pending their first CI run. Windows verdict: the client and launcher refuse on `win32`; whether grpc-js can dial a named pipe through its `unix:` target is untested.
- **Scope:** `Scout.Host.ControlPlane` launched by the supervisor; `@grpc/grpc-js` channel over the UDS path and token; one `Health` call round-trips in a Vitest test against the real host binary.
- **Success criteria:** the test passes on Linux and macOS in `ui-ci.yml`; a written verdict on Windows named pipes (shim, or stdio fallback).

### Phase 2 - Contract codegen
- **Status:** not started
- **Scope:** `ts-proto` wired into the `shared` package build; generated types replace the matching `ipc-contract.ts` shapes; a CI check that regenerated code matches the committed code.
- **Success criteria:** `bun run typecheck` clean with no hand-written duplicate of a proto message.

### Phase 3 - Screens on the control plane
- **Status:** not started
- **Scope:** providers, features, llm and whisper handlers call the client; `ui/packages/main/src/worker/http-client.ts` and the `SCOUT_HTTP=` marker are deleted; Ollama pull progress streams through the gRPC server stream.
- **Success criteria:** Providers and Features screens render live data; the "backend unavailable" state appears only when the control-plane host is down.

## Decisions pending

- Windows transport: named-pipe shim versus stdio JSON-RPC fallback. Decide in phase 1; record as a new ADR if the answer deviates from ADR 0011.

## Services affected

- [[pages/services/desktop-ui]]
- [[pages/services/host-control-plane]]
- [[pages/services/control-plane]]
- [[pages/services/control-plane-contracts]]

## Risks

- **Risk:** `@grpc/grpc-js` has no first-class named-pipe support, so Windows may need a second transport. **Mitigation:** phase 1 spike before any screen work.
- **Risk:** two .NET processes to supervise doubles the lifecycle surface. **Mitigation:** reuse the existing supervisor and restart policy for both.

## Related

- ADRs: [[pages/decisions/0011-backend-frontend-grpc-uds]], [[pages/decisions/0005-scout-desktop-m1-scope]]
- Backlog: [[pages/architecture/restructure-architecture]]
