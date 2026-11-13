---
title: "Plugin.GitHub: The audit as a job (ADR 0042, 2026-11-07)"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Plugin.GitHub service page."
page_type: service
status: active
description: "Read when a change touches: The audit as a job (ADR 0042, 2026-11-07)."
source: ["src/Scout.Plugin.GitHub/", "tests/Scout.Plugin.GitHub.Tests/"]
reliability: high
---

## The audit as a job (ADR 0042, 2026-11-07)

`start_repository_audit(owner, repo)` (`src/Scout.Plugin.GitHub/GitHubActionsPlugin.cs:562`), declared on `Scout's data folder` (`src/Scout.Plugin.GitHub/GitHubServiceCollectionExtensions.cs:152`), `stop_repository_audit` (`src/Scout.Plugin.GitHub/GitHubActionsPlugin.cs:588`), declared on the same row because it writes the job's status (`src/Scout.Plugin.GitHub/GitHubServiceCollectionExtensions.cs:154`), `pause_repository_audit` (`src/Scout.Plugin.GitHub/GitHubActionsPlugin.cs:575`), declared on the same row for the same reason (`src/Scout.Plugin.GitHub/GitHubServiceCollectionExtensions.cs:156`) and described so the model calls it only when the user asks to pause, and `get_repository_audit_status` (`src/Scout.Plugin.GitHub/GitHubActionsPlugin.cs:601`), a read, hand the repository to the host's `IRepositoryAudits` ([[pages/services/audit]]) and answer its sentence; a host without one answers "Audits are not available in this Scout.", and none of the four calls GitHub (`tests/Scout.Plugin.GitHub.Tests/GitHubActionsPluginRunTestsTests.cs`). The stop and the pause are writes, so neither is in the chat's general set; the `code-audit` skill declares both.
