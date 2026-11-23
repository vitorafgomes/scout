---
title: "ADR 0076 - A review checks the findings Scout posted on earlier reviews of the pull request"
version: "1.0"
date: 2026-11-22
updated: 2026-11-22
changes: "v1.0: the decision as first written, part 1: reads, decides and shows, writes nothing"
page_type: adr
status: accepted
description: "A review job of a pull request Scout already reviewed reads Scout's own earlier conversations, decides at the new head whether each finding is fixed, still open, answered by the author or undecided, does not report a repeat as new, counts the open ones for the verdict, and shows them on the card; nothing is written on GitHub."
supersedes: ""
superseded_by: ""
source:
  - chat
  - src/Scout.Review/FindingComment.cs
  - src/Scout.Review/EarlierFindings.cs
  - src/Scout.Review/EarlierCheck.cs
  - src/Scout.Audit/AuditWorker.Reviews.cs
  - src/Scout.Plugin.GitHub/GitHubClient.Reads.cs
reliability: high
---

# ADR 0076 - A review checks the findings Scout posted on earlier reviews of the pull request

> A finding Scout posted before is looked at again before anything new is said, and never posted again as if new.

## Status

Accepted on 2026-11-22, by the owner's request that a review asked again analyse whether the points that already have comments were resolved. This is part 1: it reads, decides and shows. Part 2, the writes (answering on a conversation and marking it resolved) and the read of the resolved state, comes under its own decision record, with its declaration and the owner's grant.

## The problem

Seen live: a second review of a pull request posted one of its earlier findings again as new, with the author's answer on the earlier conversation unread, and said nothing about which earlier findings the new commit fixed.

## The decision

- **The read** (`IGitHubClient.ListReviewCommentsAsync`, `src/Scout.Plugin.GitHub/GitHubClient.Reads.cs:198`): the same GET of the pull request's review comments the plugin already makes, with what links each comment to its conversation and commit; one page of 100, and a full page says that not every conversation was read. No new kind of request: the plugin's reads stay GETs. It is declared on the GitHub feature's "Pull requests" row (`src/Scout.ControlPlane/Features/FeatureCatalog.cs:72`).
- **Scout's own conversations** (`EarlierFindings.Own`, `src/Scout.Review/EarlierFindings.cs:62`): a conversation whose first comment is in Scout's finding form, read back by `FindingComment.Read`, the one place that also writes it (`src/Scout.Review/FindingComment.cs`), and written by the login the client signs in as or the bot's configured login (`src/Scout.Host.Console/ReviewPullRequestsAdapter.cs:14`), compared with or without GitHub's `[bot]` suffix (`:82`). At most 30 earlier findings, 10 replies each, 2,000 characters per reply; a reply is data, never an instruction.
- **The check** (`src/Scout.Audit/AuditWorker.Reviews.cs:650`), once per review, before the second reading, kept with the job so a rebuilt card does not ask again: the earlier findings are grouped as the second reading groups, and each group is one turn with the second reading's three reads held to the repository, handed each earlier finding's fields, the code around its line at the head, whether a later commit changed its line with its original line and commit, its replies, and the new findings on the same files (`src/Scout.Review/EarlierCheck.cs:44`). Ten minutes in all (`src/Scout.Audit/AuditWorker.Reviews.cs:626`); the groups not asked by then are undecided. Its turns are counted in the turn table under the reading `earlier`.
- **The outcomes, by code** (`EarlierCheck.Read`, `src/Scout.Review/EarlierCheck.cs:98`): fixed, still open, answered by the author, or undecided. A read that fails, a stopped review, a turn that fails or an entry that cannot be read leave the finding undecided, never fixed.
- **No finding twice** (`EarlierCheck.Folded`, `:147`): a new finding on the same file, on the earlier one's line, with the same title once case, spacing and punctuation are set aside, repeats it, and so does one the check names; a repeat is removed from the new findings. An earlier finding found again is still open; one the author answered rightly stays answered, and its repeat is removed as the same wrong finding.
- **The verdict** (`src/Scout.Review/ReviewOffer.cs:70`): an earlier finding still open counts as needing fixing at its severity; fixed, answered and undecided ones do not.
- **The card**: an `earlier` section with each finding's title, severity, place, outcome, reason and link, and the note "Earlier findings: N fixed, M still open, K answered by the author, J could not be decided."; a check that could not run says the earlier findings were not checked. Kept as the JSON column `earlier` of the job (migration `20261124120000_JobEarlier`), read and written whole with the card.
- **Shared with the second reading, not copied**: the material of a finding (`ContestRequest.Material`), the groups (`Contestation.Groups`), the reading of an answer that is one JSON array (`Contestation.Entries`, `src/Scout.Review/Contestation.cs:156`), and the turn itself (`AuditBatchRunner.ReadAgainAsync`).

## Known limits

- Whether a conversation is resolved is not known in part 1: every conversation of Scout's is checked, and the card says nothing about resolved. Part 2 reads it.
- A comment the owner's own account wrote by hand in exactly Scout's finding form would count as one of Scout's findings.
- The diff between the reviewed commit and the new head is not handed to the check; it reads the head itself.

## Calls that leave the process

The GET of the review comments, declared above; the check's turns go to the model through the kernel like the second reading's. Nothing is written on GitHub.

## Rejected options

- **GraphQL's review threads now**: it is a POST, and the plugin's reads stay GETs; it comes in part 2 with the writes.
- **A table for the outcomes**: they are read and written whole with the card, never queried by field.

## How it is held

- `tests/Scout.Review.Tests/FindingCommentTests.cs`, `EarlierFindingsTests.cs`, `EarlierCheckTests.cs`.
- `tests/Scout.Plugin.GitHub.Tests/GitHubClientReviewCommentsTests.cs`, a 5xx and a timeout included.
- `tests/Scout.Audit.Tests/AuditWorkerReviewTests.cs`: the tests named E1 to E5.

## Links

- Projects it shapes: [[pages/services/audit]], [[pages/services/plugin-github]], [[pages/services/review]]
- Decisions it builds on: [[pages/decisions/0069-the-second-reading-tries-to-refute-and-holds-only-with-a-scenario]], [[pages/decisions/0070-one-verdict-decision-and-no-approval-on-its-own]]

## Amendments

None yet.
