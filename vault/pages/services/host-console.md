---
title: "Host.Console"
version: "1.1"
date: 2026-10-21
updated: 2026-10-21
changes: "Re-verified against the imported development line on 2026-10-21"
page_type: service
status: active
description: "The chat host executable: composes LLM, skills, conversation, knowledge, adapters, plugins and a loopback control-plane endpoint, runs the adapters, or runs a one-shot project index."
source: ["src/Scout.Host.Console/", "tests/Scout.Host.Console.Tests/"]
reliability: high
---

# Host.Console

> The process the desktop app launches and supervises: it starts whichever adapters are configured and answers until stopped.

## To change here, read

- [[pages/invariants/pluggable-boundaries]]
- [[pages/invariants/secrets-never-in-source]]
- [[pages/invariants/local-first-default]]
- [[pages/invariants/no-executive-writes]]
- [[pages/conventions/testing]]

## Responsibility

Owns composition, the conversation worker, the readiness marker, the parent watchdog, the loopback control-plane HTTP endpoint and the `index` command. Does not own behaviour of any piece it wires.

## Public contract

- **Entry point:** `src/Scout.Host.Console/Program.cs`. `ConsoleCommand.Parse` selects run or `index <project>` (`src/Scout.Host.Console/ConsoleCommand.cs:22`); the index path builds a minimal graph (`src/Scout.Host.Console/Program.cs:14`).
- **Composition:** `AddScoutHost` (`src/Scout.Host.Console/HostComposition.cs:50`) registers in order: readiness signal (`:57`), request context (`:63`), LLM (`:65`), skills (`:69`), conversation (`:70`), knowledge (`:71`), three adapters (`:75` to `:77`), GitHub and CodeReview plugins (`:80`, `:84`), 20 daily-assistant plugins (`:89` to `:116`), SelfInfo (`:121`, off by default), control-plane domain and HTTP host (`:130`, `:131`), worker (`:133`), watchdog (`:137`). `AddScoutIndexHost` (`:153`) registers LLM and knowledge only. Plugin projects are listed in [[pages/architecture/plugin-catalogue]].
- **Runtime types:** `ConversationWorker` (`src/Scout.Host.Console/ConversationWorker.cs:14`), `IReadinessSignal` printing the `SCOUT_READY` marker on stdout (`src/Scout.Host.Console/IReadinessSignal.cs:18`), `ParentWatchdogService` (`src/Scout.Host.Console/ParentWatchdogService.cs:13`) with a stdin-EOF probe (`src/Scout.Host.Console/IParentLivenessProbe.cs:23`), `IndexEntryPoint` (`src/Scout.Host.Console/IndexEntryPoint.cs:12`).
- **Configuration keys read here:** `Scout:Adapters:Discord:BotToken`, `Scout:Adapters:Slack:BotToken`, `Scout:Adapters:Teams:AppId` (startup marker only, `src/Scout.Host.Console/Program.cs:47`); everything else is read by the wired projects. Environment variable `SCOUT__PARENT_WATCHDOG` enables the watchdog when set to `1` (`src/Scout.Host.Console/ParentWatchdogRegistration.cs:13`). Configuration root is the binary directory (`src/Scout.Host.Console/Program.cs:17`, `:32`).
- **Control-plane HTTP:** `ControlPlaneHttpHost` (`src/Scout.Host.Console/ControlPlaneHttpHost.cs:20`) serves `ProvidersService` and `FeaturesService` on a random loopback port with `HttpListener` (`:67`, `:69`) and prints `SCOUT_HTTP=127.0.0.1:<port>` on stdout (`:22`, `:79`). It checks the `x-scout-token` header against `SCOUT_CONTROLPLANE_TOKEN` in constant time (`:23`, `:24`, `:379`) and refuses to bind at all when the variable is unset (`:59`). Routes: `providers` list, status, start, stop, restart, autostart (`:253`) and `features` credentials, setup, enable, disable (`:304`); there is no model pull or delete route. This is a second transport over the same domain as [[pages/services/host-control-plane]], which is a separate gRPC process.
- **External boundaries:** stdout carries the readiness marker and the `SCOUT_HTTP` line; all logs go to stderr (`src/Scout.Host.Console/Program.cs:38`). Network boundaries belong to the wired projects.

## How it works

```mermaid
flowchart TD
    cmd["ConsoleCommand.Parse"] -->|run| full["AddScoutHost"]
    cmd -->|index| min["AddScoutIndexHost"]
    full --> w["ConversationWorker"]
    w --> ad["each IChatAdapter.StartAsync(conductor.HandleAsync)"]
    ad --> rdy["SCOUT_READY on stdout"]
    min --> ix["IndexEntryPoint -> ProjectIndexer"]
```

With no adapter registered the worker writes a hint to stderr and stops the application (`src/Scout.Host.Console/ConversationWorker.cs:25`); a failing adapter start does the same (`:52`). Readiness is signalled only after every adapter has started (`:66`).

## Invariants

- [[pages/invariants/pluggable-boundaries]]: wires every adapter and plugin at compile time and lets configuration pick (`src/Scout.Host.Console/Scout.Host.Console.csproj:16`).
- [[pages/invariants/secrets-never-in-source]]: ships `appsettings.json` with empty credentials (`src/Scout.Host.Console/appsettings.json:31`, committed content) and a user-secrets id (`src/Scout.Host.Console/Scout.Host.Console.csproj:7`).
- [[pages/invariants/local-first-default]]: committed provider is Ollama (`src/Scout.Host.Console/appsettings.json:10`).
- [[pages/invariants/no-executive-writes]]: composes every plugin the model can call, and the chat host process now also holds `OllamaProviderService`, so the token-guarded `providers/{id}/start` and `stop` routes can spawn and kill `ollama serve` (`src/Scout.Host.Console/ControlPlaneHttpHost.cs:281,285`).

## Hot spots

- `src/Scout.Host.Console/HostComposition.cs:69`: registration order matters; skills must precede conversation (comment at `:67`).
- `src/Scout.Host.Console/ConversationWorker.cs:45`: adapters start sequentially, one failure aborts the host.

## Testing footprint

`tests/Scout.Host.Console.Tests` (32 test attributes, 7 files): command parsing, the control-plane HTTP host (`ControlPlaneHttpHostTests.cs`), worker behaviour, full-graph and index-graph smoke tests with a throwing HTTP handler to prove no network on resolve (`HostCompositionSmokeTests.cs`, `IndexHostCompositionSmokeTests.cs`), and watchdog registration and service.

## Related

Direct project references (30): [[pages/services/llm]], [[pages/services/conversation]], [[pages/services/knowledge]], [[pages/services/adapter-discord]], [[pages/services/adapter-slack]], [[pages/services/adapter-teams]], [[pages/services/plugin-github]], [[pages/services/control-plane]], and 22 other plugin projects. Skills arrives through Conversation. Map: [[pages/architecture/project-map]].
