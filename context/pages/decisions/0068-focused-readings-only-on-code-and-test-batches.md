---
title: "ADR 0068 - Two focused readings, run only on batches with code or tests"
version: "1.0"
date: 2026-11-21
updated: 2026-11-21
changes: "v1.0: the decision as first written"
page_type: adr
status: accepted
description: "Beside the general reading, a review batch gets a removed reading when one of its code or test files removes lines and a requests reading when it holds a code or test file and the description lists requests; a batch of documentation, configuration or build files gets neither; their findings join the general reading's as a union."
supersedes: ""
superseded_by: ""
source:
  - chat
  - src/Scout.Review/ReviewReadings.cs
  - src/Scout.Audit/AuditBatchRunner.cs
reliability: high
---

# ADR 0068 - Two focused readings, run only on batches with code or tests

> A batch with code or tests is read three times: once in general, once for what it removes, once against what the pull request asks. Pages, prototypes and configuration are read once.

## Status

Accepted on 2026-11-21, built at the owner's request after a measurement on real pull requests. It amends [[pages/decisions/0061-the-pull-request-review-job-as-built]], whose text stays as decided.

## The problem

Measured on real pull requests, one general reading of a batch missed problems that a reading told to look at one thing found: tests deleted while the code they covered stayed, and requests of the description met in one place and not in its sibling. Sending those readings to every batch spent turns on documentation and prototype batches where they could find nothing.

## The decision

- **The two readings** (`src/Scout.Review/ReviewReadings.cs:35`). The removed reading looks only at what the patches remove, above all removed tests and checks, and asks whether what they guarded still holds at the head (`:22`). The requests reading takes the description's requests one at a time and checks each place each one applies to (`:26`). Each is one more turn of the same batch, with the same functions and filters, and its task placed before the evidence rule.
- **Only on code or tests.** A file counts when `ReviewReadings.IsCodeOrTest` (`:88`) says so: reading order 0 (source code) or 1 (tests) of `ReviewReadings.ReadingOrder` (`:59`), the one rule the planner orders batches by. The removed reading runs when the patch of one such file removes a line; the requests reading when one such file is in the batch and the description lists a request. A batch of documentation, configuration or build files gets neither.
- **Documentation at the root only.** A file is documentation by its extension anywhere, or by a `docs`, `context` or `.claude` folder at the repository's root. Before this change a folder of those names anywhere in a path made a file documentation, so `src/Data/Context/AppDbContext.cs` got no focused reading; such a file is now code.
- **The union.** A focused reading's findings join the batch's: a finding on the same path, derived line and evidence as one already there is that finding, kept with the higher severity; an id already taken is told apart by the reading's name (`src/Scout.Audit/AuditBatchRunner.cs:158`). A focused reading that fails leaves the general reading's findings as they are; the card counts the focused readings not completed.
- **Not built.** Other focused readings were tried in the same measurement; they were not built, since the problems measured were found by these two and each reading is a turn per batch.

## Calls that leave the process

None. A focused reading is a model turn under the batch's own functions.

## What follows

- **Gained:** removed tests and partly met requests are looked for on purpose.
- **Paid:** up to two more turns per code batch.
- **To watch:** a requests reading on a pull request whose description lists nothing never runs, by design.

## Rejected options

- **Every focused reading on every batch:** most batches of a large pull request are pages and prototypes, where these readings found nothing.
- **More readings:** each is a turn per batch, and the measured problems were found by these two.

## How it is held

- `tests/Scout.Review.Tests/ReviewReadingsTests.cs:13` (a removal gets the removed reading), `:37` (no code or test file, no focused reading), `:61` (a documentation folder counts at the root only).
- `tests/Scout.Audit.Tests/AuditBatchRunnerTests.cs:1217` (the removed reading runs on a batch that removes lines).

## Links

- Projects it shapes: [[pages/services/review]], [[pages/services/audit]]
- Decisions it amends: [[pages/decisions/0061-the-pull-request-review-job-as-built]]

## Amendments

None yet.
