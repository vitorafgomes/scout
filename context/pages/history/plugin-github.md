---
title: "Plugin.GitHub - history"
version: "1.0"
date: 2026-11-10
changes: "Initial history note: the test command run_pull_request_tests takes from CI"
page_type: reference
status: artefact
description: "What changed in the GitHub plugin and when."
source:
  - chat
  - src/Scout.Plugin.GitHub/GitHubActionsPlugin.Tests.cs
  - tests/Scout.Plugin.GitHub.Tests/GitHubActionsPluginRunTestsTests.cs
  - .github/workflows/ci.yml
reliability: high
---

# Plugin.GitHub - history

> Dated changes to [[pages/services/plugin-github]]; the service page states how it works now.

## 2026-11-10 - The owner stops a test run from Scout Desktop

- **Asked for:** a running container test run with a Stop icon in the bell.
- **What changed:** `RunningTests`, the runs in progress keyed by owner, repository and pull request number, each with the conversation it was asked from; `run_pull_request_tests` registers its run as soon as the request is checked and runs every step, the snapshot's download included, on a token linked to the turn's, so a Stop clicked while the snapshot downloads ends the run before any container starts, and a stop ends the run as stopped by the owner: the function answers `ran` false with a sentence saying so, and nothing is kept as the pull request's tests. Finishing and stopping are one transition under the registry's lock, so a run has one outcome.
- **Tests:** `RunningTestsTests.cs` (a running run stopped, a stop while the snapshot downloads, none running, twice, a stop as the run returns, a run that finished first).
- **Review round, same day:** `RunningTests.StopAsync` cancels the runs and waits for each to end, and the run decides how it ended: cut short before the sandbox returned, or finished, its result kept, when the sandbox had returned, because the sandbox has then written the run's record; before, a stop landing as the sandbox returned answered stopped while the record held the real result. A test invokes the function through Semantic Kernel and shows the invocation's cancellation reaching the sandbox's token.

## 2026-11-10 - The CI test line as the workflow runs it, and only a line that can run over a fresh snapshot

- **Asked for:** every `run_pull_request_tests` on this repository failed in about 30 seconds with exit 2 while CI was green, and each review said "Tests failed (exit 2)". The run's log ended in `sh: 1: Syntax error: Unterminated quoted string` after `dotnet test tests/Scout.Sandbox.Tests --configuration Release --no-build --filter "Category=Docker`.
- **Cause:** a single-line `run` value was trimmed of every `"` and `'` at both ends, so a command that merely ended in a quoted argument lost its closing quote. And the line chosen, the first CI line naming a test runner, could not have run anyway: it carries `--no-build`, and a snapshot has no build output.
- **What changed:** a single-line value is returned as written unless it is wholly wrapped in one pair of matching quotes, which are removed with `\"`, `\\` or `''` decoded. A CI `dotnet test` line with `--no-build` as a word is skipped, and, when the host's `IStackDetector` names the repository's stack, so is a line whose runner belongs to another stack; the next line is tried, else the stack's default runs. The second rule was needed because skipping the Docker line alone made `bun test ./docs` of `ui-ci.yml` the next match, which the .NET image cannot run. A filtered line stays eligible, and the conventions profile's line is never filtered.
- **Not changed:** the profile's precedence, the block scalar reading, the `./gradlew` rewrite, the sandbox's limits and network policy.
- **Tests:** `A_Single_Line_Run_Value_Is_The_Command_The_Workflow_Runs_Character_For_Character` (seven forms, the owner's line verbatim among them), `A_Block_Scalar_Run_Value_Is_Each_Line_The_Workflow_Runs_Character_For_Character`, `A_CI_Line_That_Cannot_Run_In_A_Fresh_Checkout_Is_Not_Chosen` (five rows), `A_CI_Line_Whose_Runner_Is_Not_The_Repository_s_Stack_Is_Not_Chosen` (seven rows), `A_Stack_Detection_That_Fails_Leaves_The_CI_Line_Chosen_As_Without_A_Detector` (Rule I) and `On_This_Repository_s_Own_Workflows_The_Stack_s_Default_Command_Is_Chosen`, which reads this checkout's `.github/workflows`. `Without_A_Profile_Line_The_Command_Is_The_First_CI_Step_That_Runs_A_Test_Runner` now has `dotnet test -c Release` in its block scalar instead of `dotnet test --no-build -c Release`, which is skipped now.
- **Left open:** what the default run of this repository meets in the container, and the review line for a run that could not start: [[pages/roadmap/followups]].

## Version notes: `pages/services/plugin-github.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- v1.21 (2026-11-13) present_review_findings records a review that found nothing, called with [], so the card still shows what was checked; an audit still needs at least one finding. Earlier: v1.20 (2026-11-12) run_pull_request_tests answers the runner's output up to 6,500 characters, so a failed run's failure lines reach the model before the end of the output, and its description says the output has those two sections, both untrusted data; the run kept for the review's card stays cut to the last 4,000. Earlier: v1.19 (2026-11-12) A pull request's files are read page after page, up to the 3,000 GitHub lists, so get_pull_request lists the files up to 60,000 characters and counts the rest and the diffs it left out, get_pull_request_file reads a file past the first hundred, and a later page that fails, a 404 included, fails the read; files GitHub sent no diff for are one line naming at most 100, not a section each.
- v1.18 (2026-11-12) The catalog rows carry the sentence the GitHub panel shows; five reads with Notifications.
- v1.17 (2026-11-10) RunningTests: the owner stops a pull request's test run from Scout Desktop; the turn answers that the owner stopped it and no result is kept.
- v1.16 (2026-11-10) pause_repository_audit, a write on the Scout's data folder row like the stop, hands the repository to IRepositoryAudits.PauseAsync. Earlier: run_pull_request_tests takes a single-line CI `run` value as the workflow's shell gets it (only a wrapping pair of quotes is removed) and skips a CI `dotnet test --no-build` line and, with the host's IStackDetector, a line whose runner is not the repository's stack, so this repository runs the .NET default (2026-11-10). Earlier: the executor runs the approved actions one after the other, each claimed once (2026-11-10). Earlier: v1.13 (2026-11-09) submit_pull_request_review, create_issue, the two comments and the reply are irreversible writes, a review card's Post being the approval of its review: a model call is queued with its arguments and sends nothing, and GitHubWriteRunner runs the approved action for the host executor; the four descriptions say so (ADR 0032 amended).
- v1.13 (2026-11-09) get_file_contents marks on ReadOutcome every answer that is not the file's text, so the audit does not count that file as read; the answer and the signature are unchanged.
- v1.12 (2026-11-07) each snapshot taken or reused records the repository's stack beside the snapshots through the host's IStackDetector, a failure logged and never breaking the snapshot; run_pull_request_tests passes the repository and the number for the run's record (ADR 0047 v1.4).
- v1.11 (2026-11-07) `run_pull_request_tests` turns a `./gradlew` in command position in the chosen test command into the image's `gradle --no-daemon --offline`, and a CI or profile `./gradlew test` line is now chosen at all (the old pattern needed a word character before `./`).
- v1.10 (2026-11-07) a 429, or a 403 GitHub marks as a primary or secondary rate limit, is a rate limit naming the seconds to wait, never missing token permissions, on reads and writes alike.
- v1.9 (2026-11-07) ADR 0043 and 0044: the verdict as an irreversible write run by the host executor pinned to the reviewed commit, the three comment writes, get_pull_request_checks, run_pull_request_tests over ISandboxRunner, checks, tests and selfAuthored in the review block, the optional Scout bot identity per write and repository, the audit functions over IRepositoryAudits, the IRepositorySnapshots seam.
- v1.8 (2026-11-06) present_audit_findings: an audit's findings as the same block with kind audit, no number and the known limitations; rule per finding on both functions (ADR 0040 v1.2).
- v1.7 (2026-11-06) present_review_findings: the review's findings handed to Scout as data, validated by the desktop's rules and attached to the end of the reply as the scout-findings block (ADR 0040 v1.1).
- v1.6 (2026-11-06) the repository snapshot: snapshot_repository, the two client reads, the store, the three reads served from it (ADR 0039).
- v1.5 (2026-11-06) the review and the issue posted at once under the grant; drafts and executor removed.
- v1.4 (2026-11-06) the whole-project reads, list_repository_files and search_code, and the conventions profile functions.
- v1.3 (2026-11-06) the two writes: the GitHubActions plugin, the drafts, the executor, the write client, the catalog rows under WriteClass.Executive.
- v1.2 (2026-11-06) the single-item reads for the code quality assistant (one pull request with its diffs, its comments, one issue, one file) and the name grammar every request path is checked against;
- v1.1 token file from the desktop GitHub App sign-in; GET-only test
