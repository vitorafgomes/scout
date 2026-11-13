---
paths:
  - "src/Scout.Plugin.GitHub/**"
  - "tests/Scout.Plugin.GitHub.Tests/**"
---

# Plugin.GitHub

Owns the GitHub REST client, the kernel functions and reading the token file. Does not own the sign-in, the refresh or the file itself, which the desktop main process writes ([[pages/services/desktop-ui]]), nor kernel composition ([[pages/services/llm]]).

## Rules this project must keep

- [[pages/invariants/no-executive-writes]] as [[pages/decisions/0032-per-feature-write-permissions]] reshaped it: the writes are declared rows of the `github` feature, need the owner's grant and, on GitHub, the owner's approval of each action, and are logged; everything else the plugin does is a GET (`tests/Scout.Plugin.GitHub.Tests/GitHubTokenFileHandlerTests.cs:142`, `:199`).
- [[pages/invariants/secrets-never-in-source]]: the token or the token file path comes from configuration only; blank means the plugin is absent.
- [[pages/invariants/pluggable-boundaries]]: a `KernelPlugin` singleton picked up by the shared kernel.

## Pages that govern this project

Read one when your change touches what its title names.

- [[pages/invariants/no-executive-writes|Scout performs descriptive writes only]] as [[pages/decisions/0032-per-feature-write-permissions|ADR 0032 - Features declare what they write, and the owner grants each one]] reshaped it: every write is a declared row of the `github` feature, needs the owner's grant and is logged; every write on GitHub (the review, the verdict, a comment, a reply, an issue) is `IrreversibleWrite`, so it runs only as an action the owner approved, from its card or on the Review screen, and only the writes into Scout's data folder and the local container run under the grant alone; everything else the plugin does is a GET (`tests/Scout.Plugin.GitHub.Tests/GitHubTokenFileHandlerTests.cs:142`, `:199`).
- [[pages/invariants/secrets-never-in-source|Secrets never live in source]]: the token, the token file path and the bot's token file path come from configuration only; blank means the plugin, or the bot, is absent; no answer, outcome or log line carries a token (`tests/Scout.Plugin.GitHub.Tests/GitHubBotIdentityTests.cs`).
- [[pages/invariants/pluggable-boundaries|Adapters, LLM providers and plugins are swapped by configuration]]
- [[pages/decisions/0019-github-app-device-flow|ADR 0019 - GitHub through a read-only GitHub App and the device flow]]
- [[pages/decisions/0038-code-quality-assistant-on-the-write-gate|ADR 0038 - The code quality assistant: GitHub reads and writes on the write gate, CodeQA superseded by Knowledge]]
- [[pages/conventions/testing|Testing]]

## What to read for a change

Find the row your change matches and read that page. Read one or two rows, not the table.

| If the change touches | Read |
|---|---|
| Public contract | `context/pages/services/plugin-github/public-contract.md` |
| How it works | `context/pages/services/plugin-github/how-it-works.md` |
| The writes (ADR 0038 v1.3, ADR 0043, 2026-11-07) | `context/pages/services/plugin-github/the-writes.md` |
| The pull request's checks and its tests in a container (ADR 0043, 2026-11-07) | `context/pages/services/plugin-github/the-pull-request-s-checks-and-its-tests-in-a-con.md` |
| The audit as a job (ADR 0042, 2026-11-07) | `context/pages/services/plugin-github/the-audit-as-a-job.md` |
| The conventions profile (ADR 0038 phase 4, 2026-11-06) | `context/pages/services/plugin-github/the-conventions-profile.md` |
| The repository snapshot (ADR 0039, 2026-11-06) | `context/pages/services/plugin-github/the-repository-snapshot.md` |
| The review's findings for the cards (ADR 0040 v1.1, 2026-11-06) | `context/pages/services/plugin-github/the-review-s-findings-for-the-cards.md` |
| An audit's findings for the cards (ADR 0040 v1.2, 2026-11-06) | `context/pages/services/plugin-github/an-audit-s-findings-for-the-cards.md` |
| The code quality assistant (2026-11-06) | `context/pages/services/plugin-github/the-code-quality-assistant.md` |
| Hot spots | `context/pages/services/plugin-github/hot-spots.md` |
| Testing footprint | `context/pages/services/plugin-github/testing-footprint.md` |

For where a type lives or who calls it, ask the code graph (`graphify explain "<Type>"`, `graphify affected "<Type>"`).
