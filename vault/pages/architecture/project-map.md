---
title: "Project map"
version: "1.4"
date: 2026-10-21
updated: 2026-11-02
changes: "Host.Console references ControlPlane.Contracts for the worker gate client"
page_type: longform
status: active
description: "Every project under src/ with its kind, its ProjectReference dependencies and the test project that covers it."
source: ["Scout.slnx", "src/*/*.csproj", "tests/*/*.csproj"]
reliability: high
---

# Project map

> Sixteen source projects in `Scout.slnx`; the dependency graph is shallow and acyclic. Projects under `src/old` and `tests/old` are not in the solution.

Derived from the `ProjectReference` items in each `.csproj`. Package references are listed on the service pages.

| Project | Kind | Depends on | Test project |
|---|---|---|---|
| [[pages/services/llm]] | library | none | `Scout.Llm.Tests` |
| [[pages/services/skills]] | library | none | `Scout.Skills.Tests` |
| [[pages/services/conversation]] | library | Llm, Skills | `Scout.Conversation.Tests` |
| [[pages/services/knowledge]] | library | none | `Scout.Knowledge.Tests` |
| [[pages/services/adapter-discord]] | adapter | Conversation | `Scout.Adapter.Discord.Tests` |
| [[pages/services/adapter-slack]] | adapter | Conversation | `Scout.Adapter.Slack.Tests` |
| [[pages/services/adapter-teams]] | adapter | Conversation | `Scout.Adapter.Teams.Tests` |
| [[pages/services/adapter-local]] | adapter | Conversation | `Scout.Adapter.Local.Tests` |
| [[pages/services/plugin-github]] | plugin | none | `Scout.Plugin.GitHub.Tests` |
| [[pages/services/plugin-kubernetes]] | plugin | none | `Scout.Plugin.Kubernetes.Tests` |
| [[pages/services/plugin-youtube]] | plugin | none | `Scout.Plugin.YouTube.Tests` |
| [[pages/services/transcription-whisper]] | library | Conversation | `Scout.Transcription.Whisper.Tests` |
| [[pages/services/control-plane-contracts]] | contracts | none | none of its own (`Scout.ControlPlane.Tests`) |
| [[pages/services/control-plane]] | library | none | `Scout.ControlPlane.Tests` |
| [[pages/services/host-console]] | host | Llm, Conversation, Knowledge, four adapters, Plugin.GitHub, Plugin.Kubernetes, Plugin.YouTube, Transcription.Whisper, ControlPlane.Contracts | `Scout.Host.Console.Tests` |
| [[pages/services/host-control-plane]] | host | Contracts, ControlPlane | none of its own (`Scout.ControlPlane.Tests`) |

`Scout.Host.Console` reaches Skills through Conversation. `Scout.Knowledge`, `Scout.Skills`, `Scout.Plugin.GitHub`, `Scout.Plugin.Kubernetes` and `Scout.Plugin.YouTube` do not reference `Scout.Llm`; they meet it at run time through Semantic Kernel types (`Kernel`, `KernelPlugin`, `IEmbeddingGenerator`) that the host wires together (`src/Scout.Host.Console/HostComposition.cs:40` to `:61`).

```mermaid
flowchart TD
    HC[Host.Console] --> Llm
    HC --> Conversation
    HC --> Knowledge
    HC --> Discord[Adapter.Discord]
    HC --> Slack[Adapter.Slack]
    HC --> Teams[Adapter.Teams]
    HC --> Local[Adapter.Local]
    HC --> GH[Plugin.GitHub]
    HC --> K8S[Plugin.Kubernetes]
    HC --> YT[Plugin.YouTube]
    HC --> Whisper[Transcription.Whisper]
    HC --> Contracts[ControlPlane.Contracts]
    Whisper --> Conversation
    Conversation --> Llm
    Conversation --> Skills
    Discord --> Conversation
    Slack --> Conversation
    Teams --> Conversation
    Local --> Conversation
    HCP[Host.ControlPlane] --> Contracts[ControlPlane.Contracts]
    HCP --> CP[ControlPlane]
```

## Two processes

- **Chat host:** `Scout.Host.Console` and everything it references. Run by the desktop app or by hand.
- **Control-plane host:** `Scout.Host.ControlPlane`, `Scout.ControlPlane`, `Scout.ControlPlane.Contracts`. Serves the desktop UI, and the write gate to the chat host. Until 2026-11-02 it shared no project with the chat host; since then the chat host references `Scout.ControlPlane.Contracts` for the generated `WorkerGate` client, because the worker asks the control plane before a declared write ([[pages/decisions/0011-backend-frontend-grpc-uds]], amended). It still shares no code path: the contracts project holds only the `.proto` files.

The desktop UI under `ui/` is a separate Bun and Electron workspace and is not part of `Scout.slnx` (see [[pages/conventions/testing]]).

## Related

- [[pages/invariants/pluggable-boundaries]]
- [[pages/invariants/no-executive-writes]]
