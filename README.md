# Scout

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4.svg)](https://dotnet.microsoft.com/)
[![CI](https://github.com/vitorafgomes/scout/actions/workflows/ci.yml/badge.svg)](https://github.com/vitorafgomes/scout/actions/workflows/ci.yml)

**Scout is an assistant for the people who run software: it reads your repositories and your infrastructure and reports what it finds, in your team chat or on your desktop.** It works the same with a model running on your machine and with a paid provider, so a team without an AI budget gets the same features as one with it. It never writes or edits your code, and runs your tests only inside an isolated container you enable, to say whether a pull request is good to merge. Every write it can make is declared by a feature, granted by you in the desktop app and, when it cannot be undone, approved one action at a time.

Project site: https://vitorafgomes.github.io/scout/

## Why Scout

- **Local or paid, the same Scout.** Ollama on your machine is the default and every feature runs on it; connect OpenAI, Azure, OpenRouter, Gemini, Mistral, Hugging Face or a ChatGPT plan and nothing else changes. No feature is tied to one vendor.
- **It reports, it does not act on its own.** Findings come back as cards you decide on one by one; a review, a verdict on a pull request, an issue or a video goes out only after you said so.
- **Built to be trusted.** One test per behaviour, a build at zero warnings with warnings as errors, a review gate on every change, and a write contract the code cannot bypass.

## What Scout does today

- **Answers in chat.** Ask in Discord, Slack or Teams, or in the desktop's floating chat, about a repository, a pull request, a Kubernetes cluster, or a meeting it transcribed. Scout reads and answers with citations.
- **Reviews a pull request.** The findings come back as cards; you choose, edit or dismiss each one, and Scout posts the chosen ones as one review with comments on the lines.
- **Audits a whole repository.** Against the project's own conventions, which Scout learns from the project's files. Each finding you choose becomes one issue, with what, why, where and the smallest fix.
- **Keeps a local snapshot of a repository** under its own data folder and indexes it, so reads and searches do not depend on the GitHub rate limit.
- **Desktop app.** Connects the chat clients and the AI Sources, grants each feature its reads and writes, shows the models in use, transcribes meetings, and offers a floating window to ask with text, voice, a screenshot or the text on screen.
- **Experimental: a YouTube channel operator** that researches, scripts, narrates and assembles videos and publishes only through actions you approve one by one.

## Where Scout is going

The near-term plan and the longer one live in the repository's own wiki:

- [Gap plan](vault/pages/roadmap/gap-plan.md): what blocks real use today, one change per version.
- [Future improvements](vault/pages/roadmap/future-improvements.md): a project memory that survives restarts and sends the model only what it needs, a verdict on a pull request with the project's tests run in an isolated container, an audit that covers a whole repository in batches, ideas and a roadmap proposed for a repository as cards that become issues, what each AI Source has left, Claude as a provider.
- [Architecture decisions](vault/pages/decisions/): every decision as an ADR, the newest proposed ones first.

## How it works

Two processes on your machine, one repository:

- **Scout Desktop** (`ui/`, Electron and Angular) provisions the chat clients and the AI Sources, holds the grants and approvals, and runs the worker.
- **The worker** (`src/`, .NET 10 and Microsoft Semantic Kernel) talks to the chat platforms, the LLM provider and the systems Scout reads.

Pluggable by configuration, never by code:

- **Chat adapters:** Discord, Slack, Teams (Bot Framework; needs a public HTTPS endpoint), and the desktop's local channel.
- **AI Sources:** Ollama on your machine by default, so Scout runs without paid credentials; OpenAI, Azure OpenAI, Azure AI Inference, OpenRouter, Gemini, Mistral, Hugging Face, and a ChatGPT plan through the official sign-in. The same features on every one of them.
- **Features:** GitHub, Kubernetes, YouTube, and the desktop's own, each declaring the reads and writes it performs.

Secrets never live in source: User Secrets in development, environment variables in production; `appsettings*.json` ships with names and no values.

## Download

Every release, with its checksums: https://github.com/vitorafgomes/scout/releases

- Linux x86_64: `Scout-Desktop-<version>-linux-x86_64.AppImage`, or `Scout-Desktop-<version>-linux-amd64.deb` for Debian and Ubuntu
- macOS on Apple silicon, 12 or later: `Scout-Desktop-<version>-mac-arm64.dmg`
- Windows 10 or later, 64-bit: `Scout-Desktop-<version>-win-x64.exe`

Builds are not code-signed yet. macOS asks you to open the app from System Settings the first time; Windows shows a SmartScreen prompt. Verify the download against the `SHA256SUMS-<os>.txt` files in the release.

## Install, run, test

Worker (.NET 10 SDK, pinned by `global.json`):

```bash
git clone https://github.com/vitorafgomes/scout.git
cd scout
dotnet restore
dotnet build Scout.slnx
dotnet test
```

Desktop (Bun):

```bash
cd ui
bun install
bun run test
bun run dist:linux   # or dist:mac, dist:win
```

Tests run with no extra setup. The build is at zero warnings, with warnings as errors.

## Documentation

Everything is in the in-repo wiki under `vault/`: start at [`vault/index.md`](vault/index.md). The vision is [`vault/pages/overview/vision.md`](vault/pages/overview/vision.md); the architecture is [`vault/pages/architecture/high-level.md`](vault/pages/architecture/high-level.md) and [`vault/pages/architecture/project-map.md`](vault/pages/architecture/project-map.md); the rules the code must keep are under [`vault/pages/invariants/`](vault/pages/invariants/), starting with the write contract.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) and the [Code of Conduct](CODE_OF_CONDUCT.md). To report a security issue, see [SECURITY.md](SECURITY.md).

Everything in this repository is written here. Another project may lend an idea, recorded in the wiki by mechanism, never its code.

## License

[MIT](LICENSE). Copyright (c) 2026 Vitor Gomes and Scout contributors.
