# Scout

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4.svg)](https://dotnet.microsoft.com/)

Scout is a read-only chatops assistant for Discord, Slack, and Microsoft Teams. It answers operational and development questions in chat by querying GitHub, searching indexed local repositories via vector similarity, and calling out to a configurable LLM provider. It never writes to any external system.

## Status

Pre-alpha. The core pipeline (LLM gateway, conversation, adapters, project knowledge RAG, skills) builds and tests pass on CI. The Desktop UI (Electron provisioning shell) has its own CI pipeline but is not yet in a release state.

## Architecture

The solution file is `Scout.slnx`. All active source lives under `src/`.

### Backend (`src/`)

| Project | Role |
|---|---|
| `Scout.Llm` | LLM gateway. Wraps Microsoft Semantic Kernel with a streaming-first `IChatGateway`. Selects one of eight provider implementations at startup from `Scout:Llm:Provider`. |
| `Scout.Conversation` | Platform-agnostic conversation core. Maintains per-channel, per-user in-memory history. Routes messages through the LLM gateway with streaming. |
| `Scout.Adapter.Discord` | Discord chat adapter. Self-gates on `Scout:Adapters:Discord:BotToken`. |
| `Scout.Adapter.Slack` | Slack chat adapter using Socket Mode (no public URL required). Self-gates on `Scout:Adapters:Slack:BotToken`. |
| `Scout.Adapter.Teams` | Microsoft Teams chat adapter. Self-gates on `Scout:Adapters:Teams:AppId`. |
| `Scout.Knowledge` | Project knowledge RAG subsystem. Chunks local repository files, stores embeddings in SQLite via sqlite-vec, and exposes a `search_project` Semantic Kernel plugin. Supports auto-discovery of repositories under configured root directories and optional incremental file-watch re-indexing. |
| `Scout.Skills` | Retrieval-based skills system. Loads markdown skill files from disk, selects the most relevant one per turn via cosine similarity on embeddings, and injects it into the system prompt. Also exposes skills as a Semantic Kernel plugin (`Skills`) for LLM-driven listing. |
| `Scout.Plugin.GitHub` | Read-only GitHub plugin. Exposes `list_repositories`, `list_pull_requests`, `list_issues`, and `get_repository` as Semantic Kernel functions. Self-gates on `Scout:Plugins:GitHub:Token`. |
| `Scout.ControlPlane` | Domain services for the control-plane: provider management (Ollama readiness, process launching), feature catalog, credential store, and runtime configuration. |
| `Scout.ControlPlane.Contracts` | gRPC/Protobuf contracts (`providers.proto`, `models.proto`, `features.proto`) shared between the control-plane host and its clients. |
| `Scout.Host.Console` | Main runnable host. Starts configured chat adapters and the conversation worker. Also handles the one-shot `index <project>` command. |
| `Scout.Host.ControlPlane` | ASP.NET Core host for the control-plane gRPC service. Communicates with the Desktop UI over a Unix domain socket (Linux/macOS) or a named pipe (Windows). |

### Desktop UI (`ui/`)

An Electron 41 application that provides a provisioning interface for Scout. Built with Angular 21 (zoneless, Signals), Bun as the workspace toolchain, and electron-builder for cross-platform packaging. It supervises `Scout.Host.Console` as a child process.

## LLM Providers

The active provider is selected by `Scout:Llm:Provider`. Supported values:

- `Ollama` (default)
- `OpenAI`
- `AzureOpenAI`
- `AzureAIInference`
- `Gemini`
- `Mistral`
- `HuggingFace`
- `OpenRouter`

## Requirements

- .NET SDK 10.0.100 or later (pinned in `global.json`; `rollForward: latestMinor`)
- For the `Ollama` provider: [Ollama](https://ollama.com/) running locally (default `http://localhost:11434`)
- For the Desktop UI: [Bun](https://bun.sh/) 1.2 or later (pinned to 1.3.12 in CI)

## Build and Test

```bash
dotnet restore
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
```

These are the exact commands run by `.github/workflows/ci.yml`.

## Running the Console Host

```bash
dotnet run --project src/Scout.Host.Console
```

The host reads configuration from `src/Scout.Host.Console/appsettings.json` and any environment-variable overrides (double-underscore as separator, e.g. `Scout__Adapters__Discord__BotToken`). Each adapter is self-gating: if its credentials are absent the adapter is skipped and the host continues with the remaining ones.

### One-shot index command

To index a configured project and exit immediately (without starting any adapters):

```bash
dotnet run --project src/Scout.Host.Console -- index <project-name>
```

`<project-name>` must match a `Name` value under `Scout:Projects:Items` or an auto-discovered project under a `Scout:Projects:Roots` directory.

## Configuration

All keys are under the `Scout:` prefix. Secrets must never be committed; use .NET User Secrets or environment variables in development and inject via environment variables or a secrets manager in production.

### LLM (`Scout:Llm`)

| Key | Description | Default |
|---|---|---|
| `Scout:Llm:Provider` | Active provider name (see providers list above) | `Ollama` |
| `Scout:Llm:Ollama:Endpoint` | Ollama base URL | `http://localhost:11434` |
| `Scout:Llm:Ollama:Chat:Model` | Chat model name | `llama3.1` |
| `Scout:Llm:Ollama:Embeddings:Model` | Embedding model name | `nomic-embed-text` |
| `Scout:Llm:OpenAI:ApiKey` | OpenAI API key | — |
| `Scout:Llm:OpenAI:Chat:Model` | OpenAI chat model | — |
| `Scout:Llm:AzureOpenAI:ApiKey` | Azure OpenAI API key | — |
| `Scout:Llm:AzureOpenAI:Endpoint` | Azure OpenAI resource endpoint | — |
| `Scout:Llm:AzureOpenAI:Chat:Deployment` | Azure chat deployment name | — |
| `Scout:Llm:AzureOpenAI:Embeddings:Deployment` | Azure embeddings deployment name | — |

Other provider blocks follow the same pattern: `Scout:Llm:<ProviderName>:ApiKey`, `Chat:Model` (or `Chat:Deployment` for Azure), and `Embeddings:Model`.

### Conversation (`Scout:Conversation`)

| Key | Description | Default |
|---|---|---|
| `Scout:Conversation:SystemPrompt` | System prompt prepended to every conversation turn | (built-in default) |
| `Scout:Conversation:Temperature` | Sampling temperature; forwarded to the LLM only when set | (not set) |
| `Scout:Conversation:MaxTokens` | Output token cap; forwarded to the LLM only when set | (not set) |

### Chat Adapters

| Key | Description |
|---|---|
| `Scout:Adapters:Discord:BotToken` | Discord bot token — empty disables the adapter |
| `Scout:Adapters:Slack:BotToken` | Slack bot (`xoxb-`) token — empty disables the adapter |
| `Scout:Adapters:Slack:AppLevelToken` | Slack app-level (`xapp-`) token for Socket Mode |
| `Scout:Adapters:Teams:AppId` | Microsoft App (bot) id — empty disables the adapter |
| `Scout:Adapters:Teams:AppPassword` | Microsoft App password / client secret |
| `Scout:Adapters:Teams:TenantId` | AAD tenant id (empty for multi-tenant) |

### GitHub Plugin (`Scout:Plugins:GitHub`)

| Key | Description |
|---|---|
| `Scout:Plugins:GitHub:Token` | Personal Access Token — empty disables the plugin. Read-only scopes are sufficient. |

### Project Knowledge (`Scout:Projects`)

| Key | Description | Default |
|---|---|---|
| `Scout:Projects:Items` | Array of `{ "Name": "...", "RepoPath": "/absolute/path" }` entries | `[]` |
| `Scout:Projects:Roots` | Array of directory paths scanned for repositories at startup | `[]` |
| `Scout:Projects:Watch:Enabled` | Enable incremental file-watch re-indexing | `false` |
| `Scout:Projects:Watch:DebounceMilliseconds` | Quiet window before a changed file is re-indexed | `750` |

The knowledge database is stored at `%LOCALAPPDATA%/Scout/project-knowledge.db` (Linux: `~/.local/share/Scout/project-knowledge.db`).

### Skills (`Scout:Skills`)

| Key | Description | Default |
|---|---|---|
| `Scout:Skills:Enabled` | Enable the skills retrieval system | `true` |
| `Scout:Skills:Directory` | Directory containing skill markdown files | `skills` (relative to the binary) |
| `Scout:Skills:TopN` | Maximum number of skills injected per turn | `1` |
| `Scout:Skills:SimilarityThreshold` | Minimum cosine similarity for a skill to be selected | `0.55` |

## Desktop UI

```bash
cd ui
bun install
```

See `ui/README.md` for full instructions on running in development mode, running tests, and building distributable packages (`bun run dist:linux`, `bun run dist:mac`, `bun run dist:win`).

The UI CI pipeline (`.github/workflows/ui-ci.yml`) runs on Ubuntu, macOS, and Windows on every push or PR touching `ui/**`.

## Read-only invariant

Scout enforces read-only access at the design level. The GitHub plugin exposes only listing and retrieval functions. No adapter, plugin, or knowledge operation writes to any external system. `CONTRIBUTING.md` treats this as a hard invariant — any PR introducing a write operation will not be merged.

## Documentation

All design documents, ADRs, invariants and specifications live in the in-repo
vault, `vault/`, maintained with the Claude Code harness under `.claude/`
(how it works: `vault/pages/conventions/tooling.md`).

Entry points:

- `vault/index.md` - content catalogue, start here
- `vault/pages/overview/vision.md` - vision and scope
- `vault/pages/architecture/high-level.md` - architecture
- `vault/pages/architecture/project-map.md` - projects and their dependencies
- `vault/pages/decisions/` - ADRs
- `vault/pages/invariants/` - rules the code must preserve, starting with
  `no-executive-writes.md`

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). All contributors are expected to follow the [Code of Conduct](CODE_OF_CONDUCT.md).

To report a security issue, see [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE) — Copyright (c) 2026 Chiacho Gomes.
