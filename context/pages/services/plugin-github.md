---
title: "Plugin.GitHub"
version: "1.21"
date: 2026-10-21
updated: 2026-11-12
changes: "v1.21 (2026-11-13) present_review_findings records a review that found nothing, called with [], so the card still shows what was checked; an audit still needs at least one finding.."
page_type: service
status: active
description: "GitHub plugin: fourteen read functions over a typed REST client (listing, one pull request with its diffs, comments and checks, one issue, one file, the whole tree, code search, and the two that hand a review's or an audit's findings to the reply as the block the desktop renders as cards), declared writes: into Scout's data folder and a local container under the owner's grant (the conventions profile, a repository snapshot, an audit job and a container test run), and on GitHub only as actions the owner approves one by one (a commenting review, comments, replies, issues, and the verdict, an approval or a request for changes); authenticates from a token or from the token file the desktop GitHub sign-in writes, signs the writes the owner switched to the optional Scout bot with the bot's token, and self-gates off when neither token is configured."
source: ["src/Scout.Plugin.GitHub/", "tests/Scout.Plugin.GitHub.Tests/"]
reliability: high
---

# Plugin.GitHub

> Lets the model list repositories, pull requests and issues, describe a repository, and read one pull request with its diffs and comments, one issue and one file for a code review ([[pages/roadmap/code-quality-assistant]]), as the owner's GitHub account through the Scout GitHub App ([[pages/decisions/0019-github-app-device-flow]]) or a configured token.

## Responsibility

Owns the GitHub REST client, the kernel functions and reading the token file. Does not own the sign-in, the refresh or the file itself, which the desktop main process writes ([[pages/services/desktop-ui]]), nor kernel composition ([[pages/services/llm]]).

## Rules

The rules this project must keep, and what to read before changing it, are in `.claude/rules/plugin-github.md`. Claude Code loads that file when a task touches the project.

## Topics

Read the page your change touches, not all of them.

| Page | Read when a change touches | Characters |
|---|---|---|
| [[pages/services/plugin-github/public-contract\|Public contract]] | Public contract | 10460 |
| [[pages/services/plugin-github/how-it-works\|How it works]] | How it works | 1953 |
| [[pages/services/plugin-github/the-writes\|The writes (ADR 0038 v1.3, ADR 0043, 2026-11-07)]] | The writes (ADR 0038 v1.3, ADR 0043, 2026-11-07) | 11855 |
| [[pages/services/plugin-github/the-pull-request-s-checks-and-its-tests-in-a-con\|The pull request's checks and its tests in a container (ADR 0043, 2026-11-07)]] | The pull request's checks and its tests in a container (ADR 0043, 2026-11-07) | 8599 |
| [[pages/services/plugin-github/the-audit-as-a-job\|The audit as a job (ADR 0042, 2026-11-07)]] | The audit as a job (ADR 0042, 2026-11-07) | 1588 |
| [[pages/services/plugin-github/the-conventions-profile\|The conventions profile (ADR 0038 phase 4, 2026-11-06)]] | The conventions profile (ADR 0038 phase 4, 2026-11-06) | 1246 |
| [[pages/services/plugin-github/the-repository-snapshot\|The repository snapshot (ADR 0039, 2026-11-06)]] | The repository snapshot (ADR 0039, 2026-11-06) | 4013 |
| [[pages/services/plugin-github/the-review-s-findings-for-the-cards\|The review's findings for the cards (ADR 0040 v1.1, 2026-11-06)]] | The review's findings for the cards (ADR 0040 v1.1, 2026-11-06) | 4517 |
| [[pages/services/plugin-github/an-audit-s-findings-for-the-cards\|An audit's findings for the cards (ADR 0040 v1.2, 2026-11-06)]] | An audit's findings for the cards (ADR 0040 v1.2, 2026-11-06) | 2331 |
| [[pages/services/plugin-github/the-code-quality-assistant\|The code quality assistant (2026-11-06)]] | The code quality assistant (2026-11-06) | 2900 |
| [[pages/services/plugin-github/hot-spots\|Hot spots]] | Hot spots | 1293 |
| [[pages/services/plugin-github/testing-footprint\|Testing footprint]] | Testing footprint | 2825 |

## Related

Direct project references: none. Referenced by [[pages/services/host-console]]. Map: [[pages/architecture/project-map]].
