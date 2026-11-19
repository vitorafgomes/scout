---
title: "Plugin.GitHub: The audit as a job (ADR 0042, 2026-11-07)"
version: "1.2"
date: 2026-11-13
updated: 2026-11-19
changes: "v1.2 (2026-11-19): the start passes the count of files GitHub did not list. v1.1 (2026-11-17): the review of a pull request as a job"
page_type: service
status: active
description: "Read when a change touches: The audit as a job (ADR 0042, 2026-11-07); The review as a job (ADR 0055)."
source: ["src/Scout.Plugin.GitHub/", "tests/Scout.Plugin.GitHub.Tests/"]
reliability: high
---

## The audit as a job (ADR 0042, 2026-11-07)

`start_repository_audit(owner, repo)` (`src/Scout.Plugin.GitHub/GitHubActionsPlugin.cs:642`), declared on `Scout's data folder` (`src/Scout.Plugin.GitHub/GitHubServiceCollectionExtensions.cs:153`), `stop_repository_audit` (`src/Scout.Plugin.GitHub/GitHubActionsPlugin.cs:668`), declared on the same row because it writes the job's status (`src/Scout.Plugin.GitHub/GitHubServiceCollectionExtensions.cs:157`), `pause_repository_audit` (`src/Scout.Plugin.GitHub/GitHubActionsPlugin.cs:655`), declared on the same row for the same reason (`src/Scout.Plugin.GitHub/GitHubServiceCollectionExtensions.cs:159`) and described so the model calls it only when the user asks to pause, and `get_repository_audit_status` (`src/Scout.Plugin.GitHub/GitHubActionsPlugin.cs:681`), a read, hand the repository to the host's `IRepositoryAudits` ([[pages/services/audit]]) and answer its sentence; a host without one answers "Audits are not available in this Scout.", and none of the four calls GitHub (`tests/Scout.Plugin.GitHub.Tests/GitHubActionsPluginRunTestsTests.cs`). The stop and the pause are writes, so neither is in the chat's general set; the `code-audit` skill declares both.

## The review as a job (ADR 0055)

`start_pull_request_review(owner, repo, number)` (`src/Scout.Plugin.GitHub/GitHubActionsPlugin.Reviews.cs:26`), declared on `Scout's data folder` (`src/Scout.Plugin.GitHub/GitHubServiceCollectionExtensions.cs:155`), checks the names and the number, reads the pull request with every changed file and its patch, counts the diffs its answer would leave out by the rule `get_pull_request` uses (`src/Scout.Plugin.GitHub/GitHubPlugin.Reads.cs:573`), reads the head commit's checks as passing, pending and failing counts, both lists asked together, null when they cannot be read or GitHub has more than one page (`src/Scout.Plugin.GitHub/GitHubActionsPlugin.Reviews.cs:84`), and hands all of it, with the count of changed files GitHub counts but did not list, past its 3,000, so the job counts them as not read (`src/Scout.Plugin.GitHub/GitHubActionsPlugin.Reviews.cs:56`, `tests/Scout.Plugin.GitHub.Tests/GitHubActionsPluginReviewJobTests.cs:61`, 2026-11-19, [[pages/decisions/0062-the-review-job-runs-to-its-end-in-parallel-from-the-bell]]), to the host's `IPullRequestReviews` ([[pages/services/audit/how-it-works-review-job]]), answering its sentence (`tests/Scout.Plugin.GitHub.Tests/GitHubActionsPluginReviewJobTests.cs:44`). A read that fails answers `GitHub could not be read (<exception type>); no review was started.` and a pull request GitHub does not find says so; neither starts anything (`:91`, `:108`). `get_pull_request_review_status(owner, repo, number)` (`src/Scout.Plugin.GitHub/GitHubActionsPlugin.Reviews.cs:65`), a read in the chat's general set, relays the job's rows and reads nothing of GitHub (`tests/Scout.Plugin.GitHub.Tests/GitHubActionsPluginReviewJobTests.cs:127`). A host without reviews answers "Review jobs are not available in this Scout." (`:141`). Nothing is posted on GitHub; the job writes into Scout's data folder only, and a start the write gate refuses answers the gate's reason and reads nothing (`tests/Scout.Host.Console.Tests/HostAuditTurnTests.cs:266`).
