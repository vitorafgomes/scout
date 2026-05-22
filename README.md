# Scout

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4.svg)](https://dotnet.microsoft.com/)

Open-source, read-only chatops assistant for Discord, Slack, and Teams.

Scout answers operational and business questions in chat by reading from cloud
providers, source control, and code repositories. It never writes.

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

Architecture and design rationale live at
`obsidian-vault/scout/05-operations/plans/2026-04-21-plugin-code-qa.md`.

## Install / run / test

Not available yet. See the design docs.

## Selecting an LLM

After Scout connects to Discord, the left navigation has an **LLM**
section where you can pick an Ollama model and endpoint. The choice
is persisted; restart the worker (button on the same page) to apply
the new model.

## Documentation

All design documents, ADRs, and specifications live in the Obsidian vault,
not in this repository:

`obsidian-vault/scout/`

Entry points:

- `scout/README.md` — project index
- `scout/01-overview/vision.md` — vision and scope
- `scout/02-architecture/high-level.md` — architecture
- `scout/06-decisions/` — ADRs

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). All contributors are expected to follow
the [Code of Conduct](CODE_OF_CONDUCT.md).

To report a security issue, see [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE) — Copyright (c) 2026 Vitor Gomes and Scout contributors.
