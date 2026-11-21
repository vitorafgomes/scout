---
title: "ADR 0058 - Scout approves a pull request only when every check of the commit passed"
version: "1.2"
date: 2026-11-15
updated: 2026-11-21
changes: "v1.2 (2026-11-21): amendment, no approval is offered for now (ADR 0070). v1.1 (2026-11-15): amendment: an approval also needs a pull request without conflicts whose mergeability GitHub computed. v1.0: the decision as first written"
page_type: adr
status: accepted
description: "Before an approval is sent, Scout reads the check runs and commit statuses of the reviewed commit and refuses it while one is still running, did not pass or could not be read; requesting changes and commenting are not held back."
supersedes: ""
superseded_by: ""
source:
  - chat
  - src/Scout.Plugin.GitHub/GitHubActionsPlugin.cs
  - tests/Scout.Plugin.GitHub.Tests/GitHubActionsPluginVerdictTests.cs
  - tests/Scout.Host.Console.Tests/GitHubActionExecutorTests.cs
reliability: high
---

# ADR 0058 - Scout approves a pull request only when every check of the commit passed

> Before an approval is sent, Scout reads the check runs and commit statuses of the reviewed commit and refuses it while one is still running, did not pass or could not be read; requesting changes and commenting are not held back.

## Status

Accepted on 2026-11-15 by the owner: the bot may approve a pull request only when the pipeline has every item green. It amends [[pages/decisions/0043-the-verdict-on-a-pull-request]], under which the owner's click on Approve was sent whatever the checks said.

## The problem

A review card offered Approve while three checks of the pull request were still running, and the click would have posted an approval signed by the bot. An approval tells the team the change is ready; it must not be given before the pipeline says so.

## The decision

- **The rule is in the function that sends the verdict.** `GiveVerdictAsync` (`src/Scout.Plugin.GitHub/GitHubActionsPlugin.cs:195`) is the one path of an approval, from the review card and from the model's function alike. For an approval it reads the check runs and the commit statuses at the head commit, after it has confirmed that the head is still the commit reviewed, and refuses before anything is written (`RefuseUnlessEveryCheckPassedAsync`, `src/Scout.Plugin.GitHub/GitHubActionsPlugin.cs:277`).
- **What refuses.** A check that is queued or in progress, a status that is pending, and a completed check whose conclusion is anything but `success`, `neutral` or `skipped` (failure, cancelled, timed out, action required, stale). The answer names how many and up to three of each, a name as one line of at most 60 characters: "Scout approves only when every check of the commit passed: 1 still running (build), 1 did not pass (lint).".
- **What passes.** Every check completed as `success`, `neutral` or `skipped`. A commit with no check at all may be approved: a repository without a pipeline has nothing to wait for.
- **Not read is not passed.** When GitHub counts more check runs than the one page Scout reads, or answers a full page of 100 statuses, which may hide more, the approval is refused with "The commit has more checks than Scout could read; it approves only when it has read every one.". A read that fails or times out sends nothing and is reported as the failed read it is.
- **Only the approval.** Requesting changes and posting comments read no check and are sent as before.
- **The record.** A refused approval is recorded once in the write log as `failed: refused` with the sentence, as every refusal of a verdict is.

## Calls that leave the process

None new in kind: `GET repos/{o}/{r}/commits/{sha}/check-runs` and `GET repos/{o}/{r}/commits/{sha}/status`, the reads `get_pull_request_checks` already makes and the GitHub feature's `Pull requests` row declares, now also made before an approval.

## What follows

- **Gained:** an approval signed by Scout always stands on a green pipeline.
- **Paid:** two more GitHub reads per approval; an approval clicked too early fails and has to be clicked again once the checks pass. Waiting for the checks and approving then is a change of its own.
- **To watch:** a repository with more than 100 check runs on a commit cannot be approved by Scout until the reads page.

## Rejected options

- **Only hiding the button on the card:** the card shows the checks as they were when the review ran; the function is the place no click can go around.
- **Waiting inside the worker until the checks pass:** the worker sends GitHub writes one at a time, and a wait of minutes or hours would hold every other write behind it.
- **Treating an unread check as passed:** the rule would then hold only for small pipelines.

## How it is held

- `tests/Scout.Plugin.GitHub.Tests/GitHubActionsPluginVerdictTests.cs:83`: an approval is sent when every check passed, a skipped and a neutral one included; `:97`, refused before any write while a check runs or did not pass, naming them; `:110`, refused when the commit has more checks than were read; `:121`, refused on a full page of statuses; `:132`, a check name in the answer is one line of at most 60 characters; `:142`, a request for changes is sent while checks run or fail and reads no check; `:156`, a 403 and a 502 on the read send nothing; `:167`, a timeout on the read sends nothing.
- `tests/Scout.Host.Console.Tests/GitHubActionExecutorTests.cs:332`: an approval the owner approved while a check still runs is recorded as refused, and nothing is posted.

## Links

- Projects it shapes: [[pages/services/plugin-github/the-writes]]
- Invariants it leans on: [[pages/invariants/review-writes-on-its-pull-request]], [[pages/invariants/no-executive-writes]]
- Decisions it amends: [[pages/decisions/0043-the-verdict-on-a-pull-request]]

## Amendments

### 2026-11-15: an approval also needs a pull request GitHub can merge

Extends the sentence "Before an approval is sent, Scout reads the check runs and commit statuses of the reviewed commit and refuses it while one is still running, did not pass or could not be read". Now also: "An approval is refused when GitHub says the pull request conflicts with its base branch (`mergeable` false or `mergeable_state` `dirty`) or has not computed `mergeable` yet; both come from the pull request read the verdict already makes, and the refusal comes before the checks are read." Requesting changes is not held back. Holds in `src/Scout.Plugin.GitHub/GitHubActionsPlugin.cs:229` and `:234`, [[pages/services/plugin-github/the-writes]], and `tests/Scout.Plugin.GitHub.Tests/GitHubActionsPluginVerdictTests.cs:185`, `:197`, `:209`, `:222`.

### 2026-11-21 - No approval is offered for now

- **Adds, in front of the gate's refusals,** that an approval the review does not offer is refused with "Scout approves only when the review offers approval." and the offer's reason (`src/Scout.Plugin.GitHub/GitHubActionsPlugin.cs:318`), and the review does not offer one on its own, by [[pages/decisions/0070-one-verdict-decision-and-no-approval-on-its-own]]. The check rule of this record stays in the code and applies again when the approval returns under the owner's flag.
- **How it is held:** `tests/Scout.Plugin.GitHub.Tests/GitHubActionsPluginVerdictTests.cs:67`.
