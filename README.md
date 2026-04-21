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

## Install / run / test

Not available yet. See the design docs.

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
