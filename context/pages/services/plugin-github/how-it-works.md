---
title: "Plugin.GitHub: How it works"
version: "1.1"
date: 2026-11-13
updated: 2026-11-17
changes: "v1.1 (2026-11-17): the patch budget rule and the left-out sentence"
page_type: service
status: active
description: "Read when a change touches: How it works."
source: ["src/Scout.Plugin.GitHub/", "tests/Scout.Plugin.GitHub.Tests/"]
reliability: high
---

## How it works

```mermaid
sequenceDiagram
    participant M as Model
    participant P as GitHubPlugin
    participant C as IGitHubClient
    participant G as api.github.com
    M->>P: list_issues(owner, repo)
    P->>C: ListOpenIssuesAsync
    C->>G: GET
    G-->>C: JSON
    P-->>M: one line per issue, or a clean error line
```

With a token file, `GitHubTokenFileHandler` (`src/Scout.Plugin.GitHub/GitHubTokenFileHandler.cs:14`) sets the bearer header on every request from the file, and re-reads the file whenever its write time changes (`:40`), so the token the desktop refreshes every 8 hours is used without a restart. A missing, empty, unparsable or unreadable file throws `GitHubApiException` with "GitHub is not connected. Connect it on the Features screen." before any request leaves the process (`:70`). A 401 on a request that used the file becomes "GitHub rejected the connection. Disconnect and connect GitHub again on the Features screen." (`:35`, `tests/Scout.Plugin.GitHub.Tests/GitHubTokenFileHandlerTests.cs:113`); a 401 for a configured `Token` keeps "Check the configured token" (`:129`). Neither message carries the token or the file contents.

Every function catches `GitHubApiException`, `HttpRequestException` and `OperationCanceledException` and returns a user-safe string, so a tool call never throws into the kernel (`src/Scout.Plugin.GitHub/GitHubPlugin.cs:180`, `tests/Scout.Plugin.GitHub.Tests/GitHubPluginReadsTests.cs:358`). `get_repository` assembles metadata, languages, README excerpt and top-level entries from several GETs, treating 404 as absent (`src/Scout.Plugin.GitHub/GitHubClient.cs:54`).

`get_pull_request` puts each file's diff in its answer while the diffs fit 12,000 characters (`src/Scout.Plugin.GitHub/GitHubPlugin.Reads.cs:19`). Which diffs fit, and how many are left out, is one rule, `FitPatchBudget` (`:573`), which the review job's start uses too. When any diff is left out, the answer says so in one line that names `start_pull_request_review` for reviewing the whole pull request and `get_pull_request_file` for reading one file (`:557`, `tests/Scout.Plugin.GitHub.Tests/GitHubPluginReadsTests.cs:75`); the function's description says the same (`tests/Scout.Plugin.GitHub.Tests/GitHubPluginMetadataTests.cs:128`).
