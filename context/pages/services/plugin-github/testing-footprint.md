---
title: "Plugin.GitHub: Testing footprint"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Plugin.GitHub service page."
page_type: service
status: active
description: "Read when a change touches: Testing footprint."
source: ["src/Scout.Plugin.GitHub/", "tests/Scout.Plugin.GitHub.Tests/"]
reliability: high
---

## Testing footprint

`tests/Scout.Plugin.GitHub.Tests` (456 tests on 2026-11-09, 26 files): which answers of `get_file_contents` are marked as not read and which are not, from GitHub and from a snapshot, a refused name included, and a read outside any scope answering as before (`GitHubPluginReadOutcomeTests.cs`); the rate limits, a secondary limit marked by `Retry-After` and body, by the body only (one minute even when `X-RateLimit-Reset` is far, because requests are left) or by `Retry-After` only, a 429 with and without `Retry-After`, a primary limit naming the seconds to its reset, and a streamed 403 whose body resets (`tests/Scout.Plugin.GitHub.Tests/GitHubClientTests.cs:130`, `:144`, `:161`, `:205`, `:225`, `:174`), and the same on a write (`tests/Scout.Plugin.GitHub.Tests/GitHubWriteClientTests.cs:80`); since 2026-11-07 the comments, the verdict and its crash guard, the bot identity per write and repository, the checks, the container run and the audit functions, the reads they add (`GitHubActionsPluginCommentsTests`, `GitHubActionsPluginVerdictTests`, `GitHubBotIdentityTests`, `GitHubPluginChecksTests`, `GitHubActionsPluginRunTestsTests`, `GitHubPluginFindingsEvidenceTests`, `GitHubClientPullRequestHeadTests`); before that (300 tests on 2026-11-06, 18 files): the review and audit findings functions (`GitHubPluginFindingsTests`); the snapshot store, the snapshot function and the reads served from it (a gzip tarball built in the test, `Fakes/TarGzFixture.cs`); the tree and search reads and the query guard, the conventions profile;: the writes (`GitHubActionsPluginTests`, `GitHubWriteClientTests`, `DiffLinesTests`); the single-item reads against the stub handler, 404, 5xx and timeout (`tests/Scout.Plugin.GitHub.Tests/GitHubClientReadsTests.cs`), the plugin's bounded formatting, refusals and clean error lines (`tests/Scout.Plugin.GitHub.Tests/GitHubPluginReadsTests.cs`), the name grammar (`tests/Scout.Plugin.GitHub.Tests/GitHubNamesTests.cs`); before that (66 on 2026-10-23, 6 files): client list and repository calls against a fake handler (`Fakes/`), plugin output and error mapping, function metadata, DI self-gating with header checks, and the token-file path through the real composition with a recording primary handler (`tests/Scout.Plugin.GitHub.Tests/GitHubTokenFileHandlerTests.cs:46`: bearer from the file and again after a rewrite; `:68`: missing file answers not connected and sends nothing; `:97`: a file deleted on disconnect stops the old token).
