---
title: "Control plane client"
version: "1.1"
updated: 2026-11-12
date: 2026-10-22
changes: "v1.1 (2026-11-11): phase 4 proposed, the desktop's provider account reads moved to the worker."
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

The owner decided on 2026-10-22 to keep the gRPC control plane (ADR [[pages/decisions/0011-backend-frontend-grpc-uds]]) rather than delete it. The .NET side (contracts, domain, host, 65 tests) has been complete since the restructure, but [[pages/services/desktop-ui]] then talked to a `SCOUT_HTTP=` endpoint that only the retired worker printed, so two screens were dead on every run; those dead desktop channels and the worker HTTP client were deleted on 2026-11-12, since they could never work (Phase 3). The project cannot be called finished with a tested server that nothing calls.

## Scope

- **In scope:** `@grpc/grpc-js` client in `ui/packages/main` over `unix:` on Linux and macOS; a Windows named-pipe spike; `ts-proto` codegen from `src/Scout.ControlPlane.Contracts/*.proto` replacing the hand-written shapes in `ui/packages/shared/src/ipc-contract.ts`; launching `Scout.Host.ControlPlane` beside the console worker with the per-session token; moving the providers, features, llm and whisper IPC handlers onto the client (providers deferred by the owner, see Phase 3).
- **Out of scope:** new control-plane features on the .NET side; Slack and Teams onboarding; any change to the chat path.

## Target outcome

Providers (deferred by the owner on 2026-11-11) and Features screens render live data from the control plane on Linux and macOS, with one failing-call test per gRPC method (Rule I) and the three-OS package step green.

## Status

- **As of 2026-10-23:** the Features screen runs on the control plane on Linux, verified against the real host in `bun test`; macOS is expected to behave the same, but its `ui-ci.yml` leg has not run the new tests. The app starts the host at launch, and the packaged app ships the host binary and the protos. Turning a feature on is refused by the host (`NullFeatureActivator`), and the screen says so. Detail on the Consumers section of [[pages/services/host-control-plane]] and the Control plane section of [[pages/services/desktop-ui]].
- **As of 2026-10-22:** phase 1 done on Linux: the desktop main process launches the real host and round-trips `ListProviders` over gRPC/UDS in `bun test`.
- **Next milestone:** the llm and whisper handlers on the control plane (phase 3; Providers deferred by the owner); green macOS and Windows CI legs.
- **Blocker:** none.

## Workstreams / phases

### Phase 1 - Transport spike
- **Status:** done on Linux; macOS and Windows legs pending their first CI run. Windows verdict: the client and launcher refuse on `win32`; whether grpc-js can dial a named pipe through its `unix:` target is untested.
- **Scope:** `Scout.Host.ControlPlane` launched by the supervisor; `@grpc/grpc-js` channel over the UDS path and token; one `Health` call round-trips in a Vitest test against the real host binary.
- **Success criteria:** the test passes on Linux and macOS in `ui-ci.yml`; a written verdict on Windows named pipes (shim, or stdio fallback).

### Phase 2 - Contract codegen
- **Status:** packaged protos done on 2026-10-23 (`resources/protos`, loaded at runtime by `@grpc/proto-loader`); `ts-proto` codegen not started, so `FeatureDto` and the client's wire shapes are still written by hand.
- **Scope:** `ts-proto` wired into the `shared` package build; generated types replace the matching `ipc-contract.ts` shapes; a CI check that regenerated code matches the committed code.
- **Success criteria:** `bun run typecheck` clean with no hand-written duplicate of a proto message.

### Phase 3 - Screens on the control plane
- **Status:** Features done on Linux and macOS on 2026-10-23 (macOS not yet run in CI); the dead desktop provider channels and the worker HTTP client were deleted on 2026-11-12, since they could never work; starting, stopping and autostart of the local model server are deferred by the owner (2026-11-11); llm and whisper next. The host has no restart policy yet; the launcher environment hardening landed with it.
- **Scope:** providers (deferred), features, llm and whisper handlers call the client; the worker HTTP client and the `SCOUT_HTTP=` marker are deleted (2026-11-12); Ollama pull progress streams through the gRPC server stream.
- **Success criteria:** Providers (deferred) and Features screens render live data; the "backend unavailable" state appears only when the control-plane host is down.
- **Provider lifecycle (2026-11-11):** deferred by the owner. Starting, stopping and restarting the local model server and setting its autostart left the application on 2026-11-11 and stay drawn in the prototype under a "Not supported yet" note; the LLM Server panel reads Ollama's model list through `scout:llm.listOllamaModels` ([[pages/roadmap/ollama-local-models]]). The dead desktop channels `scout:providers.list`, `status`, `start`, `stop`, `restart` and `setAutostart` and the worker HTTP client they called were deleted on 2026-11-12, since they could never work; `scout:providers.validateKey`, the cloud key check, stays. If the controls are built later, they go through the control plane's providers service, which still exists on the .NET side (the Providers RPCs Start, Stop, Restart and SetAutostart).

### Phase 4 - Provider account reads on the worker (proposed 2026-11-11, not approved)
- **Status:** not started. Recorded on 2026-11-11 after the owner asked whether every call to a provider could be centralised. Semantic Kernel already carries every call that generates text, speech or pictures ([[pages/decisions/0012-llm-communication-through-semantic-kernel]]); it has no part for listing models, checking a key, a sign-in or reading an account's cost, so those calls cannot move into it.
- **Scope:** the desktop's own reads of a provider's account move behind the provider abstraction in `Scout.Llm` and are asked for through the control plane: the key check and the model list (`ui/packages/main/src/ipc/provider-keys.ts:17`), and the credits and cost reads (`ui/packages/main/src/ipc/usage.ts:17`, `:371`, `:376`). Every call to a provider then leaves from one project, with one handling of errors and of credentials. The ChatGPT sign-in stays in the desktop (`ui/packages/main/src/oauth/chatgpt-auth.ts:16`): it needs the browser. The Ollama model management is already on the control plane (`src/Scout.ControlPlane/Providers/OllamaApiClient.cs:1`).
- **Success criteria:** `ui/packages/main` holds no provider host other than the sign-in's; a provider refusal reads the same in the chat and on the AI Sources screen.
- **Depends on:** phase 3 for Providers. It is an organisation gain with no known defect behind it, so it waits for the client and for the owner's approval.

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
