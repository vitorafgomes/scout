---
paths:
  - "src/Scout.Host.Console/**"
  - "tests/Scout.Host.Console.Tests/**"
---

# Host.Console

Owns composition, the conversation worker, the readiness marker, the parent watchdog and the `index` command. Does not own behaviour of any piece it wires.

## Rules this project must keep

- [[pages/invariants/pluggable-boundaries]]: wires every adapter and plugin at compile time and lets configuration pick (`src/Scout.Host.Console/Scout.Host.Console.csproj:16`).
- [[pages/invariants/secrets-never-in-source]]: ships `appsettings.json` with empty credentials (`src/Scout.Host.Console/appsettings.json:31`, committed content) and a user-secrets id (`src/Scout.Host.Console/Scout.Host.Console.csproj:7`).
- [[pages/invariants/local-first-default]]: committed provider is Ollama (`src/Scout.Host.Console/appsettings.json:10`).
- [[pages/invariants/no-executive-writes]]: composes every plugin the model can call, and the write gate every declared write asks; without the desktop's socket and token that gate refuses every write.
- [[pages/invariants/secrets-never-in-source]]: the worker gate token arrives only through configuration binding (`src/Scout.Host.Console/HostComposition.cs:129`), travels only in the call metadata, and leaves the process environment once read (`tests/Scout.Host.Console.Tests/WorkerGateTokenEnvironmentTests.cs:26`).

## Pages that govern this project

Read one when your change touches what its title names.

- [[pages/invariants/pluggable-boundaries|Adapters, LLM providers and plugins are swapped by configuration]]
- [[pages/invariants/secrets-never-in-source|Secrets never live in source]]
- [[pages/invariants/local-first-default|Ollama is the default LLM provider]]
- [[pages/invariants/no-executive-writes|Scout performs descriptive writes only]]
- [[pages/decisions/0028-local-quick-ask-assistant|ADR 0028 - Quick ask: talk to Scout from the operating system, over the worker's stdio]] - stdin and stdout as the quick-ask transport
- [[pages/conventions/testing|Testing]]

## What to read for a change

Find the row your change matches and read that page. Read one or two rows, not the table.

| If the change touches | Read |
|---|---|
| Entry point; Composition; Runtime types | `context/pages/services/host-console/public-contract-entry-point.md` |
| Configuration keys read here; External boundaries | `context/pages/services/host-console/public-contract-configuration-keys-read-here.md` |
| How it works | `context/pages/services/host-console/how-it-works.md` |
| Hot spots | `context/pages/services/host-console/hot-spots.md` |
| Testing footprint | `context/pages/services/host-console/testing-footprint.md` |

For where a type lives or who calls it, ask the code graph (`graphify explain "<Type>"`, `graphify affected "<Type>"`).
