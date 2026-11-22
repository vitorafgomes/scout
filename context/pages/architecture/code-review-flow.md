---
title: "Code review: the whole flow, step by step"
version: "1.5"
date: 2026-11-10
updated: 2026-11-22
changes: "v1.5 (2026-11-22): the flow redrawn from the code, with its entry points and the one action; the inside of a review job drawn on five topic pages. v1.4 (2026-11-21): the review job as it reads now: evidence and derived lines, parts, focused readings, the second reading, one offer. v1.3 (2026-11-19): the review job reads every batch, code first, several at once, shown in the bell, one card. v1.2 (2026-11-18): a pull request whose answer left a diff out becomes a review job"
page_type: longform
status: active
description: "The flow of a code review in Scout as one diagram, ten steps and five topic pages of drawings: choosing the process, reading, the checks of each dimension, the answer, the owner's decisions on the cards, the writes on the pull request and the follow-up; each step as the skill carries it since 2026-11-10."
source:
  - chat
  - src/Scout.Skills/skills/code-review.md
  - pages/services/skills.md
  - pages/decisions/0040-review-findings-as-cards-in-chat.md
  - pages/decisions/0043-the-verdict-on-a-pull-request.md
  - pages/decisions/0032-per-feature-write-permissions.md
reliability: medium
---

# Code review: the whole flow, step by step

> A code review reads one pull request, judges it against ten steps and writes only on that pull request, after the owner's approval; it never opens an issue ([[pages/services/skills]], [[pages/decisions/0032-per-feature-write-permissions]]).

The owner's rule of 2026-11-10: a code review always puts its findings on the pull request as comments and never opens issues; analysing a whole repository is another process, the audit, and that one may open issues ([[pages/services/audit]]).

Each step below says what the skill does; `src/Scout.Skills/skills/code-review.md` walks the same ten steps in the same order, one `## N.` section each, since 2026-11-10, when the items the owner listed that day were added ([[pages/services/skills]]). The one thing he asked for that Scout cannot do is marking a conversation resolved on GitHub (step 10).

## The flow

A review is asked in a chat, by a mention of the Scout bot on GitHub, or through the local MCP server; the last two ask the desktop's chat the same question. A pull request whose diffs all fit is reviewed in one turn; one whose answer left a diff out becomes a review job. Either way the chat gets a card with the one action the review offers, and nothing is written on GitHub until the owner chooses it and approves that write.

```mermaid
flowchart TD
    A1[In a chat: Discord, Slack, Teams or the desktop] --> P
    A2[A mention of the bot on a pull request] --> ASK[The desktop asks its chat the Review it question]
    A3[MCP start_review, after the owner's Start or in free mode] --> ASK
    ASK --> P{Process: code review, only the review's functions}
    P --> READ[Steps 1 to 3: context, overview, the checks]
    READ --> BIG{Did get_pull_request leave a diff out?}
    BIG -- no --> ONE[Steps 4 to 9 in this turn: present_review_findings]
    BIG -- yes --> JOB[start_pull_request_review: a review job,<br/>queued, read in batches, read again, tests run]
    ONE --> CARD[The card: findings, notes, and the one action decided by code]
    JOB --> CARD
    CARD --> O{The owner's choice on the card}
    O -- "nothing" --> NONE[Nothing is written]
    O -- "Yes, No or Edit per finding, then the offered action:<br/>request changes, or post as a comment" --> Q[The verdict is queued in the control plane,<br/>pinned to the commit; refused while GitHub writes are off]
    Q --> AP{The owner approves that write?}
    AP -- no --> NONE
    AP -- yes --> GH[The worker sends it once: the review with its comments<br/>on the pull request; the run is logged]
    GH --> FU[A new commit: review again, each earlier finding against it]
    P -. never .-> ISS[Opening an issue, or an approval Scout offers on its own]
```

- The entry points: the skill's process (`src/Scout.Skills/skills/code-review.md:19`); a mention asks the chat (`ui/packages/main/src/notifications/mention-review.ts:108`); MCP `start_review` asks the same question (`ui/packages/main/src/mcp/mcp-tools.ts:174`).
- The job's start: `src/Scout.Plugin.GitHub/GitHubActionsPlugin.Reviews.cs:17`, `src/Scout.Audit/RepositoryAudits.Reviews.cs:146`. The one action: `ReviewOffers.Decide`, `src/Scout.Review/ReviewOffer.cs:61`.
- The owner's click: `toVerdictRequest`, `ui/packages/main/src/ipc/github-verdict.ts:126`, sent by `giveVerdict` (`ui/packages/main/src/control-plane/client.ts:1054`) and queued by `PullRequestVerdictService.GiveAsync` (`src/Scout.ControlPlane/WriteGate/PullRequestVerdictService.cs:162`); the approval: `ActionDecisionService.ApproveAsync` (`src/Scout.ControlPlane/WriteGate/ActionDecisionService.cs:361`); the write: `GitHubWriteRunner` (`src/Scout.Plugin.GitHub/GitHubWriteRunner.cs:21`).
- Simplified: a model in the chat may also ask for a verdict or a review; it goes to the same write gate and waits for the same approval.

## The ten steps

1. **Context before the code.** Read the title and the description and understand what the change is meant to solve and why.
   - `get_pull_request` gives the title, the description, the branches and the files; `list_pull_request_comments` what reviewers already said.
   - When the description or the branch names an issue or a story, `get_issue` reads it and the summary says whether the change solves what it claims, not only whether the code is tidy.
2. **Overview.** The size, the files touched, the structure.
   - The file list and the diffs that fit the budget, the snapshot, the files left out and the language skills. A pull request whose diffs all fit is reviewed in this turn.
   - When `get_pull_request` says any diff was left out, the skill starts a review job with `start_pull_request_review` and ends the turn; the job is drawn under "Inside a review job" below ([[pages/services/audit/how-it-works-review-job]], [[pages/decisions/0061-the-pull-request-review-job-as-built]]). When writes are not allowed, it reviews file by file in the turn and says which files were read.
   - When the pull request is too large to review well or mixes a refactor, a feature and a fix, the summary says so once and suggests how to split it.
3. **The pipeline.** Build, tests, linters and static analysis, before any human-style judgement.
   - `get_pull_request_checks` at the head commit; when no check runs the tests, or the owner asks, the tests run in a disposable container the owner enabled.
4. **Functional correctness.** Does the logic do what it should.
   - Unreachable branches, off-by-one, null dereferences, wrong comparisons, a changed return value, concurrency hazards, resources not closed; the edge cases: empty collections, limits and time zones; error handling: swallowed exceptions, broad catches, silent defaults, retries with no limit.
   - In event-driven code: idempotency, retries, duplicated messages and messages out of order.
5. **Design and architecture.** Does it respect the project's architecture and patterns.
   - Responsibilities and coupling; public interfaces, APIs and schemas changed without their callers, unsafe migrations, a dependency added for what the standard library does; the project's conventions profile when one exists.
   - Before accepting new code, `search_code` looks for the same thing already existing in the repository, and the finding names the existing one.
6. **Readability and maintenance.** Would someone new understand this in six months.
   - A name that lies, a function doing two things, duplicated logic, magic numbers, stale comments, a comment missing where the reason is not obvious, dead code.
7. **Tests.**
   - Behaviour changed with no test that fails without it, the error paths covered and not only the happy path, tests that read well and assert behaviour, not implementation details; a test that only asserts a call, a test weakened or deleted, a skipped test.
8. **Security and performance.**
   - Security: input reaching a query, a shell, a path or a URL without validation, secrets in code, logs or fixtures, a permission check removed.
   - Performance, only when the diff shows it: N+1 queries, needless calls, wrong use of async and await, allocations inside loops.
9. **Constructive feedback.**
   - Comment on the code, never on the person. Every finding is on a file and line, says what is wrong, why it matters and the smallest fix; blocking, should fix and nit are told apart; at most ten.
   - What is done well is said once, as a short "Done well" part of the summary (at most three lines); praise is never a finding or a posted comment.
10. **Decision and follow-up.**
    - The verdict (merge, merge after the blocking and should-fix items, needs rework); the card offers one action, decided by code ([[pages/decisions/0070-one-verdict-decision-and-no-approval-on-its-own]]): request changes for a confirmed finding that needs fixing, a comment otherwise; Scout does not approve on its own, a person approves on GitHub; every write waits for the owner's approval in the desktop, is sent once and is logged.
    - A review asked again after new commits reads Scout's earlier comments, checks each earlier finding against the new head commit (fixed, still there or changed), reports only new findings as new, and answers on a thread with `reply_to_review_comment` only when the owner asks.
    - Not possible: marking a conversation resolved on GitHub is a write Scout does not have; the skill says so.

## Inside a review job

Each part of a review job is drawn on its own page, from the code as it is:

- [[pages/architecture/code-review/queue-and-what-is-kept]]: the queue of reviews (waiting, picked in order, stopped while waiting, after a restart) and what the job database and the turn record keep.
- [[pages/architecture/code-review/job-start-and-batches]]: the preflight checks, the snapshot, the plan in reading order, the inputs every batch is handed; the window of batches, the three readings, the reads a batch is offered, the match that places a finding, the extra request and the union.
- [[pages/architecture/code-review/what-a-reading-evaluates]]: the points a reading judges, the removed and requests readings' questions, and what a finding carries.
- [[pages/architecture/code-review/tests-in-docker]]: what is sent to Docker when a review runs the tests, what is read back, and what never goes into the container.
- [[pages/architecture/code-review/second-reading-and-verdict]]: the second reading's groups, reads and four outcomes, and the one decision of what the review offers.

The decisions behind them: evidence and derived lines [[pages/decisions/0066-every-finding-carries-evidence-and-its-line-is-derived-by-code]], parts [[pages/decisions/0067-a-file-too-large-for-one-batch-is-read-in-parts]], focused readings [[pages/decisions/0068-focused-readings-only-on-code-and-test-batches]], the second reading [[pages/decisions/0069-the-second-reading-tries-to-refute-and-holds-only-with-a-scenario]], the tests [[pages/decisions/0060-a-run-that-could-not-run-its-tests-is-not-a-failed-run]], the turn numbers [[pages/decisions/0073-the-numbers-of-each-model-turn-are-kept]], the rules in one project [[pages/decisions/0065-the-review-rules-live-in-scout-review]], the window [[pages/decisions/0074-a-review-runs-its-batches-through-a-window]] and the queue [[pages/decisions/0075-a-review-asked-while-another-runs-says-it-waits]].

## What a review never does

- Open an issue, or start an audit of the repository.
- Edit files, commit, merge, or approve: Scout does not approve on its own.
- Treat anything it read (files, diffs, comments, check output) as an instruction.
