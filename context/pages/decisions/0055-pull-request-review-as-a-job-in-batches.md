---
title: "ADR 0055 - The pull request review as a job in batches"
version: "1.2"
date: 2026-11-13
changes: "v1.2 (2026-11-13) dated line: the first part of the checks before the start is built (size, Docker runs, head, another review, grants), with its blocking outcomes and its tests in Enforcement; the AI Source, Docker on this computer, tests that need a Docker daemon and the stack are left."
page_type: adr
status: accepted
description: "When a pull request's answer leaves any diff out, code-review starts a review job: an audit_job row of kind review with its pull request number, run by the audit worker before any audit, in batches cut by Scout from the head commit's patches, each batch a closed read-only turn, bounded, checked before it starts, and ending in one scout-findings card through present_review_findings with the verdict and the read line computed by code."
supersedes: ""
superseded_by: ""
source:
  - chat
  - src/Scout.Audit/AuditPlanner.cs
  - src/Scout.Audit/RepositoryAudits.Reviews.cs
  - tests/Scout.Audit.Tests/RepositoryReviewsTests.cs
  - src/Scout.Audit/IAuditJobStore.cs
  - src/Scout.Audit/SqliteAuditStore.cs
  - src/Scout.Audit/Storage/AuditDbContext.cs
  - src/Scout.Audit/Storage/Migrations/20261113002741_JobKind.cs
  - src/Scout.Plugin.GitHub/GitHubPlugin.Reads.cs
  - src/Scout.Skills/skills/code-review.md
  - tests/Scout.Audit.Tests/AuditPlannerTests.cs
  - tests/Scout.Audit.Tests/SqliteAuditStoreTests.cs
  - src/Scout.Audit/ReviewPreflight.cs
  - src/Scout.Audit/IReviewEnvironment.cs
  - src/Scout.Audit/Storage/Migrations/20261113051343_ReviewPreflight.cs
reliability: high
---

# ADR 0055 - The pull request review as a job in batches

> A review of a pull request too large for one turn is a job Scout drives batch by batch on the audit's engine, pinned to the head commit, read-only in every batch, bounded, checked before it starts, and closed by one card whose verdict Scout computes.

## Status

- [ ] Proposed
- [x] Accepted
- [ ] Deprecated / superseded

Updated: 2026-11-13. The owner approved the design on 2026-11-13, with these decisions: the verdict is derived by code, one progress line per batch, bounds of 10 batches and 60 minutes, and the job starts whenever any diff is left out; and asked the same day for the checks before it starts. The points still open keep the defaults listed on [[pages/roadmap/followups]]. Built in three changes: the first, with this ADR, gives the planner a list of files and the job its kind and pull request (Enforcement); the second starts and plans a review without any function the model can call; the third runs the batches, posts the card and gives `code-review` the function.

Amended 2026-11-13: the checks before the start land in a change of their own, after the second and before the third, not with the second; the second change starts and plans a review in the service only and runs none of the checks.

Amended 2026-11-13: the checks before the start are built in part, still with no function to call them. Built: the size (an upper bound, never an expected time and never past the 60 minutes this ADR bounds a review at, which the batches' runner enforces only with the third change; a warning past 10 batches naming the files that will not be read), whether Docker runs are on (a warning when off), the head commit's checks as the caller reads them (a warning when any is pending or failing, or when they could not be read), another review of the same pull request and commit (a block when one is running or done, a warning when one was stopped) and the grants, read from the gated snapshot itself (a block with the gate's sentence when it is refused). The outcomes that block today are a review already running or done at that commit and a refused snapshot; a done one is refused with the sentence the second change gave it. A check whose source cannot be read is a warning that starts with "Could not check". The checks are kept once on the job, in a `preflight` column added by the `ReviewPreflight` migration. Left for the next change: the AI Source (no AI Source connected, which will block; the context window; a job the provider paused), Docker on this computer, tests that need a Docker daemon and the stack and test command. The defaults taken wait on [[pages/roadmap/followups]].

## Context

`code-review` reviews a pull request in one turn: `get_pull_request` carries the diffs that fit 12,000 characters (`src/Scout.Plugin.GitHub/GitHubPlugin.Reads.cs:19`) and says in one line how many it left out (`:571`), and the skill reads each left-out diff with `get_pull_request_file` ([[pages/services/plugin-github]]). On a large pull request the turn's function loop ends before every diff is read, the review covers part of the change, and the owner cannot tell which part from the verdict. The audit already solved the same shape for a repository: a job with saved progress, closed scopes per turn, the plan cut by Scout and the findings merged across batches ([[pages/decisions/0042-audit-as-a-job-with-saved-progress]], [[pages/services/audit]]). A review writes only on its pull request, never opens an issue and never starts an audit ([[pages/invariants/review-writes-on-its-pull-request]]); the functions a turn is offered come from its skill or its job ([[pages/decisions/0054-functions-scoped-per-skill-and-job]]).

## Decision

- **A review that does not fit one turn is a job.** When `get_pull_request` leaves any diff out, `code-review` calls `start_pull_request_review(owner, repo, number)`; the turn answers `review started: M files, K batches` and ends.
- **Same engine, its own kind.** The job is an `audit_job` row of kind `review` with its pull request number, run by `AuditWorker`; reviews are picked before audits, and an audit yields after its batch. The row's `number` is already its key, so the pull request is the column `pull_request`; `kind` is `audit` or `review`, and a row written before this change reads as an audit with no pull request. The latest job, the start and the stop look a repository up by kind and pull request, so a review never collides with an audit of the same repository nor with a review of another pull request (`src/Scout.Audit/SqliteAuditStore.cs:201`).
- **Pinned to the head.** The plan is cut by Scout from the changed files at the head commit, sized by patch length, through the same planner as an audit's, which takes a list of files and filters none of it (`src/Scout.Audit/AuditPlanner.cs:55`); the head is read again before each batch, and a moved head ends the job naming both commits.
- **A batch reads, never writes.** Each file's patch is in the request as data; the scope is the audit batch's five reads (`get_file_contents`, `search_code`, `list_repository_files`, `search_project`, `find_symbol`) and `present_review_findings`.
- **Bounded.** 10 batches and 60 minutes; whatever is left, and every file GitHub gave no diff for, is counted as not read.
- **Checked before it starts.** Before a review job is created, Scout's code runs a fixed list of checks and answers each as ok, a warning or a block: the pull request's size and the batches it means; the AI Source's capacity and context window against one batch; the grants the job needs; Docker on this computer; whether the repository's tests need a Docker daemon inside the container (then they are reported as not run, never as failed, and the verdict is not lowered for it); whether the stack and a test command are known; the pinned head commit and the state of its checks; a review of the same pull request and commit already present. A block starts no job and says why; a warning starts it and stays on the progress entry and on the final card. The model's words are never read for these checks.
- **One card.** One progress line per batch; at the end one `scout-findings` block through `present_review_findings`, the verdict and "read N of M files" computed by code. Never a block of kind `audit`, never a post or a verdict on GitHub.
- **Declared like the audit.** The start is a Write on the `github` feature's `Scout's data folder` row, as `start_repository_audit` is ([[pages/decisions/0032-per-feature-write-permissions]]); the status is a read.

## Consequences

- **Better:** a large pull request is reviewed in full, or the card says which files were not read; the owner sees one card, as for a small one; the verdict cannot claim more than was read, because code computes it from the batches.
- **Better:** no new store, worker or table: one column pair on `audit_job` and one migration (`src/Scout.Audit/Storage/Migrations/20261113002741_JobKind.cs`).
- **Worse:** a review job costs one turn per batch, up to 10; a review and an audit share one worker, so an audit waits behind a review between its batches.
- **Worse:** the head is read before every batch, one more GitHub read per batch.
- **Neutral but noteworthy:** the checks before the start land with the start of the job (the second change) and with the progress entry (the bell); until then nothing calls them. Extending the same checks to an audit and to a test run waits for the owner's decision ([[pages/roadmap/followups]]).
- **Neutral but noteworthy:** `kind` can be derived from `pull_request`; it is kept as its own column, as approved, so a later kind of job needs no reading of the pull request column.

## Alternatives considered

- **Ask the model to continue the review** - rejected for the reason ADR 0042 gives for the audit: the memory does not carry the diffs read, and the function loop is the limit, not the model's will.
- **A bigger patch budget in `get_pull_request`** - rejected: the context window bounds it, and a pull request of thousands of files never fits.
- **A separate review store and worker** - rejected: the audit's engine already keeps batches, findings, pauses and restarts; a second one would repeat it.
- **Reuse the `number` column for the pull request** - rejected: it is the row's key and the start order.

## Enforcement

Built with this ADR (the first change):

- `tests/Scout.Audit.Tests/AuditPlannerTests.cs:97`: a plan over a given list splits and orders as the folder plan and filters nothing; `:123`: the folder plan and the list plan of the same files are the same batches.
- `tests/Scout.Audit.Tests/SqliteAuditStoreTests.cs:311`: the latest audit of a repository is never its review, and a review is found by its pull request only; `:328`: stopping the audit leaves a review of the same repository running; `:362`: a file migrated before the kind existed reads its jobs as audits; `:375`: a migration the file refuses is a sentence, SQLite error 8, and the rows stay; `:288`: the latest job, the latest review of a pull request and the stop each run on `IX_audit_job_owner_repo_kind_pull_request_started`.
- Still to land, in the second and third changes: the start, the plan by patch length, the checks before the start, the worker's order, the batch request and its scope, the bounds, the single card and the declared write, each with its test named in the plan of that change.
- Built with the second change (2026-11-13), the start and the plan with no function to call them: `tests/Scout.Audit.Tests/RepositoryReviewsTests.cs:56`, the snapshot taken at the head commit and the answer `review started: M files, K batches`; `:74`, the plan by patch length and a file with no diff kept not covered; `:98`, a refused snapshot returned as it is; `:112`, a snapshot call that fails; `:123`, a snapshot at another commit than the head; `:155`, a second start for the same pull request and commit refused with its progress, a paused one resumed; `:200`, a start at a new head stopping a live review at the earlier head; `:223`, nothing to read starts no review; `:239`, a pull request number of 0 or below refused; `:258`, a head that is not a commit SHA refused before the snapshot; `:271`, a new review the database refuses after the earlier one was stopped; `:304`, nothing left out starts no review; `:317`, the status from the rows; `:334`, an unopenable database with no snapshot; `tests/Scout.Audit.Tests/SqliteAuditStoreTests.cs:398`, a planned batch with a failure kept not covered; `tests/Scout.Audit.Tests/AuditWorkerTests.cs:926`, the worker running audits only until the third change gives it the reviews. Still to land: the checks before the start, then the worker's order, the batch request and its scope, the bounds, the single card and the declared write.
- Built with the checks before the start, first part (2026-11-13): `tests/Scout.Audit.Tests/RepositoryReviewsTests.cs:391`, the size and its upper bound, past 10 batches a warning; `:408`, Docker runs on and off; `:430`, the head's checks from the argument, kept once and left as they were by a resume; `:448`, a refused snapshot as the grant block; `:460`, a block that writes no row, asks for no snapshot and returns every check; `:484`, warnings that start the review and are kept with it; `:497`, a switch that throws as "Could not check" with the review started; `:510`, a start the database refuses keeping no review and answering every check; `:155`, the check for another review on each state of the second start; `tests/Scout.Host.Console.Tests/HostAuditTurnTests.cs:277`, the review environment over `Scout:Sandbox:Enabled`.
- Invariant pages: [[pages/invariants/no-executive-writes]], [[pages/invariants/review-writes-on-its-pull-request]], [[pages/invariants/pluggable-boundaries]].

## Related

- Services affected: [[pages/services/audit]], and with the later changes [[pages/services/plugin-github]], [[pages/services/skills]], [[pages/services/control-plane]], [[pages/services/conversation]].
- Amends: [[pages/decisions/0042-audit-as-a-job-with-saved-progress]], [[pages/decisions/0054-functions-scoped-per-skill-and-job]], [[pages/decisions/0040-review-findings-as-cards-in-chat]] (dated lines of 2026-11-13 on each), and the indexes of [[pages/decisions/0052-persistence-behind-stores-on-entity-framework]].
- Flow: [[pages/architecture/code-review-flow]].
