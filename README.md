# Scout

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4.svg)](https://dotnet.microsoft.com/)
[![CI](https://github.com/vitorafgomes/scout/actions/workflows/ci.yml/badge.svg)](https://github.com/vitorafgomes/scout/actions/workflows/ci.yml)

**Scout is an assistant for the people who run software: it reads your repositories and your infrastructure and reports what it finds, in your team chat or on your desktop.** It works the same with a model running on your machine and with a paid provider, so a team without an AI budget gets the same features as one with it. It never writes or edits your code, and runs your tests only inside an isolated container you enable, to say whether a pull request is good to merge. Every write it can make is declared by a feature, granted by you in the desktop app and, when it cannot be undone, approved one action at a time.

Project site: https://vitorafgomes.github.io/scout/

> **Status: early, in active testing.** Scout is used day to day on Linux only. Features marked *Ready* work in that use, but bugs are expected; *Experimental* features are new and may change or break. The macOS and Windows installers are built by CI and have not been tested by anyone yet. Please report what you find in the [issues](https://github.com/vitorafgomes/scout/issues).

## Why Scout

- **Local or paid, the same Scout.** Ollama on your machine is the default and every feature runs on it; connect OpenAI, Claude, Azure, OpenRouter, Gemini, Mistral, Hugging Face or a ChatGPT plan and nothing else changes. No feature is tied to one vendor.
- **It reports, it does not act on its own.** Findings come back as cards you decide on one by one; a review, a verdict on a pull request, an issue or a video goes out only after you said so.
- **Built to be trusted.** One test per behaviour, a build at zero warnings with warnings as errors, a review gate on every change, and a write contract the code cannot bypass.

## What Scout does today

**Ready** (used day to day on Linux; bugs are still expected):

- **Answers in chat.** Ask in Discord, Slack or the desktop's floating chat about a repository or a pull request. Scout reads and answers with citations.
- **Reviews a pull request.** The findings come back as cards; you choose, edit or dismiss each one, and Scout posts the chosen ones as one review with comments on the lines.
- **Keeps a local snapshot of a repository** under its own data folder and indexes it, so reads and searches do not depend on the GitHub rate limit.
- **AI Sources.** Ollama on your machine, OpenAI, OpenRouter and Claude, with what each has used shown on its card.
- **Desktop app.** Connects the chat clients and the AI Sources, grants each feature its reads and writes, and asks for your approval of each irreversible action, in Approvals or right in the chat.
- **Voice in the desktop.** Dictation into the question box and replies read aloud, with the voice chosen in Models.

**Experimental** (new or lightly used; expect rough edges):

- **A verdict on a pull request** with the project's tests run in a disposable, isolated container you enable.
- **Audits a whole repository** as a job that keeps its progress, against the project's own conventions; each finding you choose becomes one issue.
- **Scout's own GitHub App**, so reviews and issues are signed by the Scout bot instead of your account.
- **Conversation mode** (talk and listen) and **meeting transcription**, kept locally.
- **Kubernetes** reads, and **Teams** (needs a public HTTPS endpoint).
- **Other AI Sources**: Azure OpenAI, Azure AI Inference, Gemini, Mistral, Hugging Face and a ChatGPT plan.
- **A YouTube channel operator** that researches, scripts, narrates and assembles videos and publishes only through actions you approve one by one.

## Where Scout is going

The plan lives in the repository's own wiki:

- [Future improvements](context/pages/roadmap/future-improvements.md): a project memory that survives restarts and sends the model only what it needs, ideas and a roadmap proposed for a repository as cards that become issues, an audit with validated findings, faster transcription on the GPU, meeting detection and speaker labels, and the desktop screens on Angular Material.
- [Architecture decisions](context/pages/decisions/): every decision as an ADR, the newest proposed ones first.

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

Linux is the only platform tested so far; the macOS and Windows installers are built but untested.

- Linux x86_64: `Scout-Desktop-<version>-linux-x86_64.AppImage`, or `Scout-Desktop-<version>-linux-amd64.deb` for Debian and Ubuntu
- macOS on Apple silicon, 12 or later: `Scout-Desktop-<version>-mac-arm64.dmg`
- Windows 10 or later, 64-bit: `Scout-Desktop-<version>-win-x64.exe`

On Linux, Scout keeps credentials only in a keyring service such as GNOME Keyring or KWallet, running and unlocked in your session; without one it saves no credential, and a banner says so. Scout also refuses Electron's basic store (selected by `--password-store=basic`), which protects data with a fixed, publicly known password: it then saves nothing and does not use the credentials saved before. Start or install a keyring service, then restart Scout.

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

Everything is in the in-repo wiki under `context/`: start at [`context/index.md`](context/index.md). The vision is [`context/pages/overview/vision.md`](context/pages/overview/vision.md); the architecture is [`context/pages/architecture/high-level.md`](context/pages/architecture/high-level.md) and [`context/pages/architecture/project-map.md`](context/pages/architecture/project-map.md); the rules the code must keep are under [`context/pages/invariants/`](context/pages/invariants/), starting with the write contract.

## Contributing

See [CONTRIBUTING.md](.github/CONTRIBUTING.md) and the [Code of Conduct](.github/CODE_OF_CONDUCT.md). To report a security issue, see [SECURITY.md](.github/SECURITY.md).

Everything in this repository is written here. Another project may lend an idea, recorded in the wiki by mechanism, never its code.

## License

[MIT](LICENSE). Copyright (c) 2026 Vitor Gomes and Scout contributors.
