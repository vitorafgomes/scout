---
title: "Host.ControlPlane"
version: "1.2"
date: 2026-10-21
updated: 2026-10-23
changes: "Consumers: the desktop app starts the host at launch and its Features screen calls the Features service; packaged host and protos"
page_type: service
status: active
description: "ASP.NET gRPC host that serves the control-plane domain to the desktop UI over a Unix socket or named pipe, guarded by a per-session token."
source: ["src/Scout.Host.ControlPlane/", "tests/Scout.ControlPlane.Tests/", "ui/packages/main/src/control-plane/"]
reliability: high
---

# Host.ControlPlane

> The local door the desktop app knocks on: gRPC over a user-only socket, with a token.

## To change here, read

- [[pages/invariants/no-executive-writes]]
- [[pages/invariants/secrets-never-in-source]]
- [[pages/services/control-plane]]
- [[pages/services/control-plane-contracts]]
- [[pages/conventions/testing]]

## Responsibility

Owns the transport, authentication and gRPC adapters over [[pages/services/control-plane]]. Does not listen on TCP and does not run the chat loop ([[pages/services/host-console]]).

## Public contract

- **Entry points:** `AddControlPlaneHost` (`src/Scout.Host.ControlPlane/ControlPlaneServiceRegistration.cs:17`) and `MapControlPlaneServices` (`:38`); process entry `src/Scout.Host.ControlPlane/Program.cs:8`.
- **gRPC services:** `ProvidersGrpcService` (`src/Scout.Host.ControlPlane/Services/ProvidersGrpcService.cs:14`), `ModelsGrpcService` (`src/Scout.Host.ControlPlane/Services/ModelsGrpcService.cs:13`), `FeaturesGrpcService` (`src/Scout.Host.ControlPlane/Services/FeaturesGrpcService.cs:13`); mapping helpers `FeatureMapping` and `ProviderMapping`.
- **Security:** `SessionToken` (`src/Scout.Host.ControlPlane/Security/SessionToken.cs:11`) read from or minted at startup; `TokenAuthInterceptor` (`src/Scout.Host.ControlPlane/Security/TokenAuthInterceptor.cs:12`) accepts the token in an `authorization` bearer header or `x-scout-token` and compares in constant time (`src/Scout.Host.ControlPlane/Security/SessionToken.cs:62`).
- **Configuration and environment:** `Scout:ControlPlane:SocketPath` (`src/Scout.Host.ControlPlane/Program.cs:25`); environment variable `SCOUT_CONTROLPLANE_TOKEN` (`src/Scout.Host.ControlPlane/Security/SessionToken.cs:14`); `XDG_RUNTIME_DIR` for the socket directory (`src/Scout.Host.ControlPlane/Transport/ControlPlaneEndpoint.cs:28`); the domain keys listed on [[pages/services/control-plane]].
- **External boundaries:** Unix domain socket `controlplane.sock` on Linux and macOS, named pipe `scout-controlplane` on Windows (`src/Scout.Host.ControlPlane/Transport/ControlPlaneEndpoint.cs:12,15`); filesystem permissions on the socket and its directory (`:41`, `:95`).

## How it works

```mermaid
sequenceDiagram
    participant UI as Desktop UI
    participant K as Kestrel (UDS or pipe)
    participant I as TokenAuthInterceptor
    participant S as gRPC service
    participant D as ControlPlane domain
    UI->>K: h2c call with token
    K->>I: intercept
    I-->>UI: Unauthenticated when token wrong
    I->>S: continue
    S->>D: ProvidersService / FeaturesService / OllamaProviderService
```

Startup refuses to run on Unix when the native `stat` self-test fails, because the socket anti-hijack pre-flight depends on it (`src/Scout.Host.ControlPlane/Program.cs:35`). After Kestrel binds, the socket mode is tightened to owner read and write (`src/Scout.Host.ControlPlane/Program.cs:58`, `src/Scout.Host.ControlPlane/Transport/ControlPlaneEndpoint.cs:100`). On Windows the pipe is restricted to the current user (`src/Scout.Host.ControlPlane/Program.cs:17`).

## Invariants

- [[pages/invariants/no-executive-writes]]: `PullModel` and `DeleteModel` (`src/Scout.Host.ControlPlane/Services/ModelsGrpcService.cs:22,59`) and the start and stop calls are reachable by any holder of the token.
- [[pages/invariants/secrets-never-in-source]]: the session token is minted or read from the environment, never from a file in the repository.

## Hot spots

- `src/Scout.Host.ControlPlane/Transport/NativeUnix.cs:15`: native `stat` interop for socket ownership checks.
- `src/Scout.Host.ControlPlane/Transport/ControlPlaneEndpoint.cs:54`: pre-flight on an existing socket path.
- `src/Scout.Host.ControlPlane/Security/TokenAuthInterceptor.cs:25`: the interceptor covers unary and streaming handlers; a new RPC kind must be added here.

## Testing footprint

Covered by `tests/Scout.ControlPlane.Tests` (66 test attributes in total, 10 files): `Security/SessionTokenTests.cs`, `Security/TokenAuthInterceptorTests.cs`, and `Integration/` tests that start the host on a Unix socket (`ControlPlaneUdsIntegrationTests.cs`) and check the gRPC boundary (`ControlPlaneBoundaryTests.cs`). The `Transport/` test folder is empty.

## Consumers

The desktop main process is the one client. It starts the host when the app starts and stops it on quit; the Features screen calls the `Features` service through it. `Providers` is only exercised by tests, and nothing calls `Models` yet.

- **Lifecycle.** `connectControlPlane` (`ui/packages/main/src/control-plane/launcher.ts:208`), called from `ui/packages/main/src/main.ts:299`, launches the host, waits up to 10 s for the socket file (`ui/packages/main/src/control-plane/launcher.ts:167`) and opens the client. Every failure becomes `ControlPlaneUnavailableError`, and the Features screen then says the control plane is not running. A failure after the spawn, such as a socket that never appears, kills the host it launched. When the host exits mid-session the client is closed, so the next Features call fails at once as unavailable instead of waiting out its deadline; `dispose()` closes the client and kills the host. There is no restart policy and no parent-death watchdog (`ui/packages/main/src/control-plane/launcher.ts:120`).
- **Launcher.** `launchControlPlane` (`ui/packages/main/src/control-plane/launcher.ts:95`) spawns the host and sets two environment variables on the child: `Scout__ControlPlane__SocketPath`, a per-process socket `controlplane-<pid>.sock` under `$XDG_RUNTIME_DIR/scout` or a per-uid `scout-<uid>` folder under the temp directory (`ui/packages/main/src/control-plane/launcher.ts:87`, `:125`), and `SCOUT_CONTROLPLANE_TOKEN`, a fresh 64-hex-character token (`ui/packages/main/src/control-plane/launcher.ts:118`). The token travels only in the child's environment and in call metadata; the launcher never logs it. Because the explicit socket path makes the host skip its own directory setup, the launcher secures the socket directory itself before it mints the token or spawns: it refuses a symbolic link, a non-directory or a directory owned by another uid, and tightens a directory open to group or others to `0700` (`ui/packages/main/src/control-plane/launcher.ts:65`). The child inherits the parent environment minus every variable that could move its listener or override its socket path or token (`ui/packages/main/src/control-plane/launcher.ts:57`): `SCOUT_CONTROLPLANE_TOKEN`, `Scout__ControlPlane__SocketPath`, `URLS`, `HTTP_PORTS`, `HTTPS_PORTS` and every `Kestrel__` key, matched in any casing, with `:` or `__`, and with or without the `ASPNETCORE_` or `DOTNET_` prefix. `extraEnv` applies next and the launcher's own socket path and token last. Without that filter an inherited `Kestrel__Endpoints__Http__Url` made the real host listen on TCP beside the socket; `ui/packages/main/test/control-plane-integration.test.ts` checks that the port now refuses connections.
- **Binary.** `resolveControlPlaneBinary` (`ui/packages/main/src/worker/resolve-binary.ts:51`) honours `SCOUT_CONTROLPLANE_BIN`, then the packaged `worker/<rid>/` folder beside the console worker, then `src/Scout.Host.ControlPlane/bin/Release/net10.0/` and `bin/Debug/net10.0/` under the repository root. `ui/build/scripts/publish-worker.sh:14` publishes the host into that packaged folder.
- **Client.** `createControlPlaneClient` (`ui/packages/main/src/control-plane/client.ts:148`) dials `unix:<absolute path>` with `@grpc/grpc-js` and insecure channel credentials, waits for the channel before each call under the same deadline (`ui/packages/main/src/control-plane/client.ts:199`), and sends the token as `x-scout-token` metadata on every call (`ui/packages/main/src/control-plane/client.ts:201`). It maps `UNAVAILABLE`, `DEADLINE_EXCEEDED` and a channel that never becomes ready to `ControlPlaneUnavailableError`, `UNAUTHENTICATED` to `ControlPlaneUnauthenticatedError`, and `UNIMPLEMENTED` to `ControlPlaneNotImplementedError` (`ui/packages/main/src/control-plane/client.ts:95`); other codes pass through as the raw gRPC error. It loads `providers.proto` and `features.proto` with `@grpc/proto-loader` at runtime (camelCase field names, enums as strings), from `resources/protos` when packaged (`ui/build/electron-builder.yml:40`). It maps `Feature` messages onto the renderer's `FeatureDto` (`ui/packages/main/src/control-plane/client.ts:126`): an unset optional string becomes `null`, `has_scope_matrix` and `has_setup_steps` decide between a list and `null`, and an `UNSPECIFIED` status becomes `error`. `setupFeature` splits the renderer's settings into upserts and `cleared_settings` for the keys set to `null` (`ui/packages/main/src/control-plane/client.ts:221`).
- **What the host does with Features today.** `ListFeatures` returns `github` then `kubernetes`. `SetCredentials` keeps the values in `InMemoryCredentialStore`, because no host registers a keyring store, and validates nothing, because no host registers a credential validator. `Enable` answers `UNIMPLEMENTED` because `NullFeatureActivator.CanActivate` is false (`src/Scout.ControlPlane/Features/IFeatureActivator.cs:31`, `src/Scout.ControlPlane/Features/FeaturesService.cs:260`). `Disable`, `DeleteCredentials` and a `Setup` that carries settings write the feature state file, `Scout/features.json` under the user's local application data folder unless `Scout:Features:StateStore:FilePath` is set. Gaps in [[pages/roadmap/followups]].
- **Tests.** `ui/packages/main/test/control-plane-integration.test.ts` launches the real host binary with the feature state file in a temp folder and checks four things: `ListProviders` returns `OLLAMA` then `LM_STUDIO`; a wrong token is rejected as `UNAUTHENTICATED`; `ListFeatures` returns GitHub and Kubernetes; `SetCredentials` with a `pat` moves GitHub from `needs-credentials` to `disabled` while `Enable` fails with `ControlPlaneNotImplementedError`. It also checks that the state file lands in the temp folder and that the developer's own `features.json` is unchanged. `ui/packages/main/test/control-plane-client.test.ts` runs the Features client against an in-process grpc-js server on a temp socket. CI builds the host before the UI tests (`.github/workflows/ui-ci.yml:100`).
- **Windows.** The host listens on the named pipe `scout-controlplane` on Windows. The launcher returns `unsupported` on `win32` and spawns nothing (`ui/packages/main/src/control-plane/launcher.ts:100`), and the client refuses before creating a channel (`ui/packages/main/src/control-plane/client.ts:151`), so on Windows the Features screen says the control plane is not running. `@grpc/grpc-js` documents no named-pipe target. Its `unix:` resolver passes the path to Node's `net.connect`, which accepts `\\.\pipe\` names on Windows, so a pipe target may work, but nobody has run it on Windows. The choice between dialling the pipe through the `unix:` target, a named-pipe shim and a stdio fallback is pending.

## Related

Direct project references: [[pages/services/control-plane-contracts]], [[pages/services/control-plane]]. Map: [[pages/architecture/project-map]]. Consumer: the desktop launcher and client in the Consumers section above, phases 1 and 3 of [[pages/roadmap/control-plane-client]]; see [[pages/services/desktop-ui]].
