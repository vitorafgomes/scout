---
title: "ADR 0059 - The owner approves once, and Scout gives the approval when the checks pass"
version: "1.2"
date: 2026-11-15
updated: 2026-11-20
changes: "v1.2 (2026-11-20): amendment, one action per commit becomes one per request (ADR 0063). v1.1 (2026-11-19): amendment, a conflict ends the wait and the approval needs GitHub to say the pull request can be merged; line citations re-pointed. v1.0: the decision as first written"
page_type: adr
status: accepted
description: "On a review card whose checks still run, Approve when the checks pass is the owner's approval of that one verdict; the desktop keeps it, reads the pull request and its checks every minute, gives it once every check passed, and ends it when a check fails, a new commit arrives, the pull request closes, two hours pass or the owner cancels."
supersedes: ""
superseded_by: ""
source:
  - chat
  - context/prototypes/desktop/Main.dc.html
  - ui/packages/main/src/github/waiting-approvals.ts
  - ui/packages/main/src/ipc/github-verdict.ts
  - ui/packages/main/src/notifications/sources.ts
  - ui/packages/renderer/src/app/core/review-findings.component.ts
  - ui/packages/renderer/src/app/core/review-findings.ts
reliability: high
---

# ADR 0059 - The owner approves once, and Scout gives the approval when the checks pass

> On a review card whose checks still run, Approve when the checks pass is the owner's approval of that one verdict; the desktop keeps it, reads the pull request and its checks every minute, gives it once every check passed, and ends it when a check fails, a new commit arrives, the pull request closes, two hours pass or the owner cancels.

## Status

Accepted on 2026-11-15 by the owner, who chose it over a bell notice alone and approved the artboard's states. It builds on [[pages/decisions/0058-an-approval-needs-every-check-passed]], under which an approval clicked while a check runs is refused, and amends [[pages/decisions/0043-the-verdict-on-a-pull-request]]: the click is still the approval of one action, sent later.

## The problem

With ADR 0058 an approval clicked before the pipeline ends fails, and the owner has to come back and click again. The owner asked whether Scout could check later by itself.

## The decision

- **One click, kept by the desktop.** On a card where no check failed and some still run, the button reads "Approve when the checks pass". The click sends the verdict request, pinned to the reviewed commit, to main over `scout:github.approveWhenChecksPass` (`ui/packages/main/src/ipc/github-verdict.ts:369`), and main keeps it in `WaitingApprovals` (`ui/packages/main/src/github/waiting-approvals.ts:129`). Only an approval may wait; at most 20 wait at once.
- **Read every minute.** Main reads the pull request and, at its head commit, the check runs and the commit statuses, with the owner's token, and judges them as the worker does: a check not completed is running; a completed one that is not success, neutral or skipped failed; more runs than read, or a full page of statuses, is unread. An answer with status 200 that is not in the shape GitHub documents, a list or a count missing or an item without its state, is unread too: what Scout could not read never passes.
- **Given when every check passed.** With nothing running, failed or unread, a commit with no check included, main gives exactly the request the owner clicked, through the verdict path the click on Approve uses. The worker's rule of ADR 0058 still judges it before anything is posted. The verdict's statuses carry `afterChecks`, and the card and the bell say "after the checks passed".
- **Ended, and said.** A failed check ends it and names the check; a head commit that moved ends it, because the review read the earlier one; a closed pull request ends it; two hours end it. Each is a bell entry and a line on the card. The owner's Cancel, on the card or on the bell's entry, ends it without an entry; a Cancel that arrives while the approval is being given changes nothing, and it ends as approved.
- **One action per commit.** An approval does not start waiting once a verdict or a review was sent on that commit, and a verdict or a review the owner sends from the card ends the one that waits.
- **A read that fails never approves.** A refused token, a rate limit, a server error, a timeout or an unreadable answer leaves the approval waiting; a 403 or 429 waits what GitHub asks. Without a GitHub connection nothing is sent.
- **Ended with the connection.** A change of the GitHub connection, a disconnect or another account, ends every waiting approval and gives none (`ui/packages/main/src/main.ts:576`): an approval is given only under the connection it was asked under.
- **Kept across a restart.** The waiting approvals are in the vault under `github.waiting-approvals`, a key only main reads, with the time the owner clicked; the two hours count from that click, and a saved time in the future counts from now. A vault that cannot be read resumes nothing; one that cannot be written leaves the approval waiting in memory.
- **Who may ask.** `scout:github.approveWhenChecksPass` and `scout:github.cancelWaitingApproval` answer only the main and quick-ask windows, on Scout's own page and from the main frame, as `scout:github.giveVerdict` does ([[pages/decisions/0053-electron-fuses-and-ipc-sender-check]]); the bell's cancel action is refused from any other sender before anything is cancelled (`ui/packages/main/src/main.ts:501`). A repository name with a `.` or `..` segment is refused.

## Calls that leave the process

- From main, with the owner's token, reads only: `GET /repos/{o}/{r}/pulls/{n}`, `GET /repos/{o}/{r}/commits/{sha}/check-runs?per_page=100` and `GET /repos/{o}/{r}/commits/{sha}/status?per_page=100`, once a minute per waiting approval. They are the reads the GitHub feature's `Pull requests` row declares.
- The approval itself is the approved action it already was, sent by the worker.

## What follows

- **Gained:** the owner approves once; the pull request is approved when its pipeline ends, with no second visit.
- **Paid:** three GitHub reads a minute for each waiting approval, for at most two hours; the approval waits only while Scout Desktop is open.
- **To watch:** the wait lives in the desktop, not in the worker; a review as a job may later move it.

## Rejected options

- **Waiting inside the worker:** it sends GitHub writes one at a time, so a wait would hold every other write.
- **A notice only, and a second click:** the owner asked for the approval to follow by itself.
- **Approving a later commit:** the review read one commit; a new one needs a new review.

## How it is held

- `ui/packages/main/test/github-waiting-approvals.test.ts:184`: given once every check passed, and not before; `:242`, exactly the request the owner approved, also after a restart; `:259`, a failed check; `:305`, a moved head; `:336`, closed; `:442`, two hours; `:462`, cancel; `:484`, resume; `:540`, each of the three reads failing with a 401, a 403 or 429 with Retry-After, a 502, a timeout, a reset and an unreadable body; `:563`, a Cancel during the give; `:584`, a change of the connection; `:597`, a saved time in the future; `:608`, a vault that cannot be read; `:626`, a vault that refuses the save; `:637`, no connection; `:648`, unread checks; `:679`, each answer that is not in GitHub's shape; `:709`, the twenty-first; `:731`, a give that fails.
- `ui/packages/main/test/ipc-github-verdict.test.ts:447`: no wait once a verdict or a review was sent on that commit; `:467`, a verdict sent from the card ends the wait; the sender rows refuse a sub-frame, another page and the operator window on both channels.
- `ui/packages/main/test/notifications-voice.test.ts:382`: the bell's Cancel from a window that may not give a verdict cancels nothing.
- `ui/packages/main/test/notifications-sources.test.ts`: one row per bell entry.
- `ui/packages/renderer/src/app/core/review-findings.component.spec.ts:2155`: the card draws each state of the artboard; `ui/packages/renderer/src/app/core/review-findings.spec.ts`: the checks are judged as the worker judges them.
- `ui/e2e/tests/approve-when-checks-pass.spec.ts`: the click, the waiting state and Cancel against the real main.

## Links

- Projects it shapes: [[pages/services/desktop-github/verdict-from-the-review-card]]
- Invariants it leans on: [[pages/invariants/no-executive-writes]], [[pages/invariants/secrets-never-in-source]]
- Decisions it builds on: [[pages/decisions/0058-an-approval-needs-every-check-passed]], [[pages/decisions/0043-the-verdict-on-a-pull-request]], [[pages/decisions/0053-electron-fuses-and-ipc-sender-check]]

## Amendments

### 2026-11-19 - A conflict ends the wait, and the approval needs GitHub's word that the pull request can be merged

[[pages/decisions/0058-an-approval-needs-every-check-passed]] refuses an approval over a conflict, or before GitHub computed whether the pull request can be merged. As first written, this decision gave the waiting approval as soon as the checks passed, so on a conflicted pull request it was given and refused every minute until the two hours, and the card said no reason.

- **Replaces, under "Given when every check passed",** the sentence "With nothing running, failed or unread, a commit with no check included, main gives exactly the request the owner clicked, through the verdict path the click on Approve uses." It now reads: "With nothing running, failed or unread, a commit with no check included, and with GitHub saying the pull request can be merged (`mergeable` is `true`), main gives exactly the request the owner clicked, through the verdict path the click on Approve uses. While GitHub has not said so (`mergeable` null, absent or not a boolean) the approval keeps waiting and is read again the next minute, until the two hours end it."
- **Replaces, under "Ended, and said",** the sentence "A failed check ends it and names the check; a head commit that moved ends it, because the review read the earlier one; a closed pull request ends it; two hours end it." It now reads: "A failed check ends it and names the check; a head commit that moved ends it, because the review read the earlier one; a closed pull request ends it; a pull request that conflicts with its base branch (`mergeable` false, or `mergeable_state` `dirty`) ends it at the read that shows it, before any check is read and also while checks still run; two hours end it."
- **Calls that leave the process:** none new. Both values come from the `GET /repos/{o}/{r}/pulls/{n}` main already made.
- **Where it holds:** the read and the two rules, `ui/packages/main/src/github/waiting-approvals.ts:343`, `:365`; the reason `conflict` on `GitHubWaitingApproval`, `ui/packages/shared/src/ipc-contract.ts:1408`; the bell's entry "Not approved: <pr> has conflicts with its base branch", `ui/packages/main/src/notifications/sources.ts:469`; the card's line "Not approved: the pull request has conflicts with its base branch.", `ui/packages/renderer/src/app/core/review-findings.component.ts:1383`; the Main artboard's state `awConflict`. It is described on [[pages/services/desktop-github/verdict-from-the-review-card]].
- **How it is held:** `ui/packages/main/test/github-waiting-approvals.test.ts:354`, a conflict, alone or with checks still running, ends as `conflict` at once, gives nothing and reads no check; `:400`, an answer that does not say whether the pull request can be merged keeps it waiting, the next read that says so gives it, and two hours end it; `ui/packages/main/test/notifications-sources.test.ts:486`, the bell's entry; `ui/packages/renderer/src/app/core/review-findings.component.spec.ts:2031`, the card's line and no approve button.

### 2026-11-20 - One action per request, not per commit

By [[pages/decisions/0063-what-scout-writes-on-a-pull-request-follows-the-templates]], from the owner's decision of 2026-11-19, a commit can carry more than one verdict.

- **Replaces, under "The decision",** the sentence "One action per commit. An approval does not start waiting once a verdict or a review was sent on that commit, and a verdict or a review the owner sends from the card ends the one that waits." It now reads: "A verdict or a review the owner sends from the card ends the approval that waits; a different verdict at the same commit is its own action, and an identical request answers the action already there."
- **Where it holds:** `src/Scout.ControlPlane/WriteGate/PullRequestVerdictService.cs:116`.
- **How it is held:** `tests/Scout.ControlPlane.Tests/WriteGate/PullRequestVerdictServiceTests.cs:127`, `:155`.
