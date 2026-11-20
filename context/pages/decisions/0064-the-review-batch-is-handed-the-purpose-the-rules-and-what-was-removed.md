---
title: "ADR 0064 - The review batch is handed the purpose, the project's rules and what was removed"
version: "1.2"
date: 2026-11-20
updated: 2026-11-20
changes: "v1.2 (2026-11-20): a review with no room beside the inputs does not start; the reported size is the final plan. v1.1 (2026-11-20): the constant block first and the batches planned in the room the inputs leave, corrected. v1.0: the decision as first written"
page_type: adr
status: accepted
description: "A pull request review keeps, at its start, what the pull request says it does, the project's own rule files at the reviewed commit and what the pull request removed, computed by code; every batch is handed them as lines of JSON data, each bounded by the length it is sent at, and the card counts what was removed."
supersedes: ""
superseded_by: ""
source:
  - chat
  - src/Scout.Audit/ReviewInputs.cs
  - src/Scout.Audit/AuditBatchRunner.cs
  - src/Scout.Audit/AuditWorker.Reviews.cs
  - src/Scout.Audit/RepositoryAudits.Reviews.cs
  - src/Scout.Plugin.GitHub/GitHubActionsPlugin.Reviews.cs
  - src/Scout.Host.Console/PullRequestReviewsAdapter.cs
  - src/Scout.Skills/skills/code-review.md
reliability: high
---

# ADR 0064 - The review batch is handed the purpose, the project's rules and what was removed

> Each batch of a pull request review judges its patches against what the pull request says it does, the project's own written rules and what the pull request removed, three inputs Scout's code computes once at the start, keeps with the job and hands to every batch as data.

## Status

Accepted on 2026-11-20, by the owner's decision of 2026-11-19 that a review batch must see more than its patches. It amends [[pages/decisions/0061-the-pull-request-review-job-as-built]] and [[pages/decisions/0062-the-review-job-runs-to-its-end-in-parallel-from-the-bell]], whose texts stay as they were decided.

## The problem

A batch was handed its files' patches and the titles of earlier findings, nothing else. It could not tell a change that does what the pull request claims from one that does not, did not know the rules the project wrote down, and saw a deleted file or a deleted test only as a patch of minus lines, if its batch held it at all.

## The decision

This record replaces ADR 0061's "Its request carries each file's patch as one line of JSON data" and ADR 0062's "The turns share the pull request read once, the head read before each wave, and the titles of the findings known when the wave started". A batch's request now opens with the job's constant block, the purpose and the rule files, each under its own sentence (`src/Scout.Audit/AuditBatchRunner.cs:63`, `:68`) as one line of JSON through the batch's encoder or one sentence that says it is missing, built once per run (`:279`) and the same for every batch, so a provider can cache it; after the batch's header and the scope sentence (`:57`) come what was removed (`:75`). All of it stays in the user message, never the system message, since it is text from outside.

- **The purpose.** The title and description of the pull request, read by the start that already reads it, and the issue its description names: a closing keyword first, else the first `#n`, never the pull request's own number, read once with `GET repos/{owner}/{repo}/issues/{n}` (`src/Scout.Plugin.GitHub/GitHubActionsPlugin.Reviews.cs:73`, `:90`). An issue GitHub fails or does not find is marked unread and the review starts all the same; a number that is a pull request is no issue. The plugin's `PullRequestPurpose` and the audit library's `ReviewPurpose` stay separate types, copied field by field by the host (`src/Scout.Host.Console/PullRequestReviewsAdapter.cs`).
- **The rules.** `ReviewRules.Read` (`src/Scout.Audit/ReviewInputs.cs:44`) reads from the snapshot at the reviewed commit, in a fixed order, `AGENTS.md`, `CLAUDE.md` and the contributing guides, then the `*.md` files of `.claude/rules`, `docs/conventions`, `docs/rules`, `context/pages/invariants` and `context/pages/conventions`. A rule file the pull request itself changes is marked `changedHere`, and the batch is told to judge that change and not rely on it, since the author can write the file at the reviewed commit. A failed read marks the rules unreadable and the review starts.
- **What was removed.** `ReviewRemoved.From` (`ReviewInputs.cs:162`) tells a deleted file from a deleted or emptied test file, and counts a test file whose patch removes more test declarations than it adds. For a deleted or emptied test file it names up to three files of the same stem still in the snapshot. A batch is handed the removals of its own files and of the code they tested.
- **Kept at the start.** The start builds the three inputs (`src/Scout.Audit/RepositoryAudits.Reviews.cs:160`) and keeps them on the job row, in the new nullable column `inputs` of the migration `20261119120000_ReviewInputs`. A resumed review keeps the inputs it started with. The worker reads them once per run (`src/Scout.Audit/AuditWorker.Reviews.cs:62`); a read the database refuses leaves the review running for the next wake, and no batch runs without its inputs. A job started before this change has none and is asked as before, but for the scope sentence.
- **The card.** The card's notes add Scout's sentences on the inputs (`ReviewInputs.cs:282`), counts only, never a path or a word of the pull request, each counted over every removal and agreeing with its number.

### Bounds

- **Characters, the first cut, which keeps the text readable:** a title and the issue's title at 300 characters, a description and the issue's body at 4,000 (`ReviewInputs.cs:264`), a rule file at 6,000, at most 12 rule files and 16,000 characters of rules (`:25`-`:28`), at most 20 rule files named as not included with the rest counted (`:26`), at most 200 removals with the rest counted (`:143`).
- **Sent length, the guarantee.** A text's characters do not bound what it is sent as: a quote or backslash takes two characters on the line of JSON and a control character six. So each line is also bounded by its length as sent: the purpose at 10,000, the rules at 18,000 and the removals at 4,000 (`AuditBatchRunner.cs:261`, `:264`, `:267`). A line over its bound is made smaller and serialized again until it fits (`:357`): the longer body is cut and marked cut, a rule file's text is cut and marked cut, a path not included or a rule file is dropped and counted, a removal is dropped and counted. With every input at its bound the inputs take at most 34,500 characters: 32,000 of data and Scout's own sentences, measured at 1,916. The plan counts them: a batch's patches are planned in the room the inputs leave under `MaxBatchCharacters` (`src/Scout.Audit/RepositoryAudits.Reviews.cs:164`, `AuditBatchRunner.cs:322`), so a request stays within the cap beside the lines always outside it; a patch with no room beside the inputs is not read, as a diff larger than one batch; when no patch has room, the review is refused with the sentence of one with nothing to read and no job is kept (`src/Scout.Audit/RepositoryAudits.Reviews.cs:166`); the size the start reports and keeps is the final plan's; and a review whose inputs hold nothing to hand is planned and asked as before.

### What is honest about the counts

A test declaration is counted from the lines of the patch with one pattern, not by parsing the language (`ReviewInputs.cs:149`), and a test file is matched to its code by its name (`:216`). A declaration split over lines, or a framework the pattern does not name, is missed; a test that moved to another file counts as removed; a namesake elsewhere counts as the code left behind. The batch is told so in its sentence, and a finding stands only when its patches show it.

## Calls that leave the process

One: `GET repos/{owner}/{repo}/issues/{n}`, a read, at most once per start and none when the description names no issue. The title and the description come from the pull request read the start already makes. The rule files and the file names are read from the snapshot under Scout's data folder. The `inputs` column is written in the transaction `start_pull_request_review` already owns, under the `github` feature's grant of Scout's data folder.

## Rejected options

- **Reading the inputs again on every wake.** A paused or restarted review would judge its later batches against other inputs than its first.
- **The rule text in the system message.** Text from the repository would sit beside Scout's instructions instead of under a sentence that names it as data.
- **A new table.** One nullable column on the job row holds what the job reads back whole.
- **Bounding by characters alone.** An input made of characters that escape could grow a request several times past its bound.

## How it is held

- `tests/Scout.Audit.Tests/ReviewInputsTests.cs:66`, `:88`, `:151`, `:188`, `:301`, `:330`, `:343`: the bounds, the changed-here mark, the counts from patch lines, the code left behind, the number of each note, the counts past the bound, and the rule files past 20.
- `tests/Scout.Audit.Tests/AuditBatchRunnerTests.cs:685`, `:702`, `:723`: each input is one line of data that a fence, a line break or a line separator cannot break; `:851`, a job without inputs adds no line; `:876` and `:910`, the sent-length bounds.
- `tests/Scout.Audit.Tests/RepositoryReviewsTests.cs:918`, `:939`, `:969`: the start keeps the inputs, starts with unreadable rules, and a resume keeps the first inputs.
- `tests/Scout.Audit.Tests/AuditWorkerReviewTests.cs:1052`, `:1076`, `:1118`, `:1150`: the same inputs after a pause and a new worker, each batch its own removals, the card's notes, and a refused read.
- `tests/Scout.Audit.Tests/SqliteAuditStoreTests.cs:438`, `:470`, `:486`: the round trip, the migration, and an unreadable value.
- `tests/Scout.Plugin.GitHub.Tests/GitHubActionsPluginReviewJobTests.cs:162`, `:180`, `:203`, `:214`: the purpose with its issue, a failed or missing issue, no link, and a pull request number.
- `tests/Scout.Host.Console.Tests/PullRequestReviewsAdapterTests.cs:28` and `tests/Scout.Host.Console.Tests/HostAuditTurnTests.cs:266`: the status and the purpose cross the host and reach the job row.
- `tests/Scout.Skills.Tests/BundledSkillsTests.cs:604`, `:613`, `:622`, `:631`: the sentences of `code-review.md` that judge removals, purpose and written rules.

## Links

[[pages/decisions/0061-the-pull-request-review-job-as-built]], [[pages/decisions/0062-the-review-job-runs-to-its-end-in-parallel-from-the-bell]], [[pages/services/audit/how-it-works-review-inputs]].
