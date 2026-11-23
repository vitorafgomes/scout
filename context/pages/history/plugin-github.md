---
title: "Plugin.GitHub - history"
version: "1.7"
date: 2026-11-10
updated: 2026-11-21
changes: "v1.7 (2026-11-21): the file read by lines, the search at a reference, one verdict decision (ADRs 0070, 0071). v1.6 (2026-11-20): the review note, the review inputs and an unreadable answer. v1.5 (2026-11-19): the review start counts the files GitHub did not list. v1.4 (2026-11-17): a pull request whose diffs were left out is reviewed as a job. v1.3 (2026-11-15): no approval over a conflict; unrunnable in the answer and the block. v1.2 (2026-11-15): an approval needs every check of the commit passed. v1.1 (2026-11-14): the verdict's record carries its tests line, and GetVerdict answers who signed it."
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


## 2026-11-21 - The file read by lines, the search at a reference, one verdict decision

- **What it was:** a file read answered its first 20,000 characters only; `search_code` searched the newest snapshot or the default branch; the gate decided the verdict on its own reading; the verdict function's texts named approval and the card's buttons.
- **What it is:** a file is read by lines with a header that says how to read on; a search at a reference reads that snapshot and asks GitHub nothing; the verdict follows `ReviewOffers.Decide`, which does not offer approval on its own; with no test run kept the reason says no test ran here; the verdict function's texts say what it does today ([[pages/decisions/0070-one-verdict-decision-and-no-approval-on-its-own]], [[pages/decisions/0071-the-file-read-by-lines-and-the-search-at-a-reference]]).
- **Tests:** new tests in `GitHubPluginReadsTests` and `GitHubActionsPluginVerdictTests`; changed on purpose: the expected file-read headers, the verdict reasons with no test run kept, and the stack record's log line, which now names the kind of failure only.

## 2026-11-20 - The review note, the review inputs and an unreadable answer

- **Asked for:** the owner's decisions of 2026-11-19 ([[pages/decisions/0063-what-scout-writes-on-a-pull-request-follows-the-templates]], [[pages/decisions/0064-the-review-batch-is-handed-the-purpose-the-rules-and-what-was-removed]]).
- **What it was:** the review and the verdict posted a body the desktop or the model wrote; a success GitHub answered with an unreadable body escaped as a `JsonException` and was recorded as a failure that sent nothing.
- **What it is:**
  - `ReviewNote` composes the note and each comment from `findings_json`; the note ends with its digest marker, and a verdict already given is matched by it ([[pages/services/plugin-github/the-review-note]]).
  - `GitHubVerdictRunner.PreviewAsync` composes the note a verdict would post and sends nothing.
  - `start_pull_request_review` reads the issue the description names, at most once, and hands the review job the purpose and each file's status.
  - A success with an empty or unreadable body is a failure saying "GitHub did not give a readable answer after the request was sent, so it may already be there: check the repository before asking again." (`src/Scout.Plugin.GitHub/GitHubWriteClient.cs`).
- **Tests:** `ReviewNoteTests`, `ReviewNoteInlineTests`, `GitHubReviewNoteWritesTests`, `GitHubActionsPluginReviewJobTests` (four new), `GitHubWriteClientTests::A_Write_GitHub_Accepts_With_An_Unreadable_Answer_Says_It_May_Already_Be_There`.

## 2026-11-19 - The review start counts the files GitHub did not list

- **Found:** an outside review of the change. A pull request with more changed files than GitHub lists, at most 3,000, was reviewed as if whole: the job planned and counted only the listed files and could end "ready to merge, N of N files read".
- **What changed:** `start_pull_request_review` passes the count of changed files GitHub counts but did not list, a new `notListed` argument of `IPullRequestReviews.StartAsync`, which the host's adapter relays to the audit library (`src/Scout.Plugin.GitHub/GitHubActionsPlugin.Reviews.cs:56`, `src/Scout.Plugin.GitHub/Abstractions/IPullRequestReviews.cs:21`, `src/Scout.Host.Console/PullRequestReviewsAdapter.cs:13`). The audit library keeps them as one batch not read ([[pages/decisions/0062-the-review-job-runs-to-its-end-in-parallel-from-the-bell]]).
- **Tests:** `tests/Scout.Plugin.GitHub.Tests/GitHubActionsPluginReviewJobTests.cs:61`; 543 tests in the project.

## 2026-11-17 - A pull request whose diffs were left out is reviewed as a job

- **What it was:** `get_pull_request` said how many diffs it left out and pointed to `get_pull_request_file`; nothing started the review job.
- **What changed:** `start_pull_request_review(owner, repo, number)` reads the pull request, counts the diffs left out by the same budget rule as `get_pull_request` (`src/Scout.Plugin.GitHub/GitHubPlugin.Reads.cs:573`), reads the head commit's checks as counts, and hands them to the host's reviews (`src/Scout.Plugin.GitHub/GitHubActionsPlugin.Reviews.cs:26`); `get_pull_request_review_status` relays the job's rows (`:64`). The start is a declared write on `Scout's data folder` (`src/Scout.Plugin.GitHub/GitHubServiceCollectionExtensions.cs:155`). The left-out sentence and the function's description name the start. Nothing is posted on GitHub.
- **Tests:** `tests/Scout.Plugin.GitHub.Tests/GitHubActionsPluginReviewJobTests.cs` (7 tests, 11 cases), `tests/Scout.Plugin.GitHub.Tests/GitHubPluginReadsTests.cs:75`, `tests/Scout.Plugin.GitHub.Tests/GitHubPluginMetadataTests.cs:128`; 542 tests in the project.

## 2026-11-15 - No approval over a conflict, and a run that could not run its tests says so

- **Asked for:** the same review of a repository whose integration tests use Testcontainers reported those tests as failed, and the card offered Approve on a pull request that conflicted with its base branch.
- **What it was:** `run_pull_request_tests` answered such a run as any failed run; an approval was refused only on a moved head, an author signing their own pull request or a check not passed.
- **What it is:** the answer, the run kept for the head commit and the review block's `tests` object carry `unrunnable` when the sandbox set it, and the function's description tells the model that such a run did not fail ([[pages/decisions/0060-a-run-that-could-not-run-its-tests-is-not-a-failed-run]], [[pages/services/plugin-github/the-pull-request-s-checks-and-its-tests-in-a-con]]). `PullRequestHead` carries GitHub's `mergeable` and `mergeable_state`, read by the same `GET pulls/{n}`; an approval is refused on `mergeable` false or `mergeable_state` `dirty` ("The pull request has conflicts with its base branch; Scout approves only a pull request that can be merged.") and while `mergeable` is null ("GitHub has not said whether the pull request can be merged. If it is still open, approve again in a moment."), after the moved-head check and before the checks are read; requesting changes is not held back ([[pages/services/plugin-github/the-writes]], [[pages/decisions/0058-an-approval-needs-every-check-passed]]).
- **Tests changed on purpose:** the fakes that serve the pull request to an approval now say it can be merged, or the approval would be refused as not yet computed: the verdict tests' default head (`Mergeable: true`), the two HTTP fakes of `GitHubBotIdentityTests`, and those of `GitHubActionExecutorTests` and `GitHubActionExecutorSecretsTests` (`"mergeable":true`).

## 2026-11-15 - An approval needs every check of the commit passed

- **Asked for:** the owner's rule that the bot approve a pull request only when the pipeline has every item green, after a review card offered Approve while three checks were still running ([[pages/decisions/0058-an-approval-needs-every-check-passed]]).
- **What changed:** before an approval `GiveVerdictAsync` reads the check runs and commit statuses at the head commit and refuses while one is queued, in progress or pending, completed with anything but success, neutral or skipped, or not read because the commit has more checks than one page; the sentence names how many and up to three of each. A commit with no check may be approved; a request for changes reads no check; a failed read sends nothing.
- **Tests:** `Scout.Plugin.GitHub.Tests` 510 to 519, eight on the rule, its failing reads, a full page of statuses and a check name kept to one bounded line; `Scout.Host.Console.Tests` 355 to 356, an approval approved while a check runs is recorded as refused and nothing is posted; the fakes of the approving tests answer the two check reads.

## 2026-11-14 - The verdict's record carries its tests line; GetVerdict answers who signed it

- **Asked for:** the bell shows a given verdict's detail, "Signed as <account> · the tests line was posted as a comment", "... · the tests line was not posted", or just "Signed as <account>" when the verdict carried no tests line. The owner chose to keep the tests line's outcome in the verdict's own record, one approval and one log row ([[pages/decisions/0043-the-verdict-on-a-pull-request]], amendment of 2026-11-14), over a row of its own.
- **What changed:** `GitHubVerdictRunner.PostTestsLineAsync` answers true when the tests line is on the pull request's conversation, posted now or found already there for a verdict given before, and false when the verdict carried none; what it posts and reads is unchanged.
- **Tests:** through the executor, `tests/Scout.Host.Console.Tests/GitHubActionExecutorTests.cs` (`A_Verdict_Found_Already_Given_Posts_Its_Tests_Line_Only_When_The_Signer_Has_Not_Posted_It_And_Either_Way_Records_It_As_Posted`).

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
