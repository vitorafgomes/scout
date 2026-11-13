---
title: "Plugin.GitHub: The conventions profile (ADR 0038 phase 4, 2026-11-06)"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Plugin.GitHub service page."
page_type: service
status: active
description: "Read when a change touches: The conventions profile (ADR 0038 phase 4, 2026-11-06)."
source: ["src/Scout.Plugin.GitHub/", "tests/Scout.Plugin.GitHub.Tests/"]
reliability: high
---

## The conventions profile (ADR 0038 phase 4, 2026-11-06)

`get_project_conventions` (`src/Scout.Plugin.GitHub/GitHubActionsPlugin.cs:729`) reads, and `remember_project_conventions` (`:760`) writes, the Markdown profile of a repository's own rules, one JSON file per repository under `Scout/github-profiles` (`src/Scout.Plugin.GitHub/GitHubProfileStore.cs`; or `Scout:Plugins:GitHub:ProfilesDirectory`), 40 to 20,000 characters, dated and with the chat it came from. The write is declared on the `github` feature's `Scout's data folder` row, so it runs only under the owner's GitHub write grant and is logged; the `code-audit` skill reads the profile before judging any code and saves it after discovering it from the project's files or after the owner corrects a rule in chat (`tests/Scout.Plugin.GitHub.Tests/GitHubActionsPluginTests.cs:164`).
