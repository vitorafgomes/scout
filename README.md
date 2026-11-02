# Scout

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4.svg)](https://dotnet.microsoft.com/)

Open-source chatops assistant for Discord, Slack, and Teams.

Scout answers operational and business questions in chat by reading from cloud
providers, source control, and code repositories. Every write a feature
performs is declared, granted by the owner in the desktop and, when it
cannot be undone, approved one action at a time; the first features are
read-only.

## Download

Project site: https://vitorafgomes.github.io/scout/

Every release, with its checksums: https://github.com/vitorafgomes/scout/releases

- Linux x86_64: `Scout-Desktop-<version>-linux-x86_64.AppImage`, or
  `Scout-Desktop-<version>-linux-amd64.deb` for Debian and Ubuntu
- macOS on Apple silicon, 12 or later: `Scout-Desktop-<version>-mac-arm64.dmg`
- Windows 10 or later, 64-bit: `Scout-Desktop-<version>-win-x64.exe`

Builds are not code-signed yet. macOS asks you to open the app from System
Settings the first time; Windows shows a SmartScreen prompt. Verify the
download against the `SHA256SUMS-<os>.txt` files in the release.

## Status

Pre-alpha. Design in progress.

## Stack

- .NET 10
- Microsoft Semantic Kernel
- Pluggable chat adapters (Discord first, then Slack, then Teams)
- Pluggable LLM providers (Ollama local, Azure OpenAI, OpenAI)

## CodeQA plugin (optional)

CodeQA runs retrieval-augmented generation over one configured GitHub
repository so Scout can answer natural-language questions about the code.
The pipeline is strictly read-only: Scout pulls file contents, builds an
in-memory vector index, and composes answers with citations.

Minimum configuration under `Scout:Plugins:CodeQA:`:

- `Repository:Owner`, `Repository:Name`, `Repository:Branch`
- `Embeddings:Model` (defaults to `nomic-embed-text` on Ollama)

GitHub PAT setup:

- Create a fine-grained personal access token with `Contents: Read` only.
- Any write-scope token violates Scout's read-only invariant — never use one.
- Export the token as `SCOUT_GITHUB_TOKEN` before starting the worker.

One-shot reindex (useful for debugging; the worker also auto-indexes on
startup when configured):

```bash
dotnet run --project src/Scout.Host.Worker -- index
```

Example Discord interaction: `@Scout where is the retriever wired?`

Architecture and design rationale live in the vault:
`vault/pages/domain/code-qa.md`.

## Install / run / test

Not available yet. See the design docs.

## Selecting an LLM

After Scout connects to Discord, the left navigation has an **LLM**
section where you can pick an Ollama model and endpoint. The choice
is persisted; restart the worker (button on the same page) to apply
the new model.

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

See [CONTRIBUTING.md](CONTRIBUTING.md). All contributors are expected to follow
the [Code of Conduct](CODE_OF_CONDUCT.md).

To report a security issue, see [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE) — Copyright (c) 2026 Vitor Gomes and Scout contributors.
