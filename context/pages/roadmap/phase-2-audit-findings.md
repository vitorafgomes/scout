---
title: "Phase 2: the findings of Scout's audit of its own repository, lane by lane"
version: "1.1"
date: 2026-11-10
changes: "The lanes replaced by what landed, how a finding is handled, and the final build (2026-11-10). Earlier: initial initiative page"
page_type: roadmap
status: active
description: "Tracker of pull request 120: the 98 findings Scout's audit opened on this repository and the prototype screens migrated beside them, with the branch, the state and the next step of each lane, the decisions that wait for the owner and the order to resume."
source:
  - chat
  - pages/roadmap/prototype-migration.md
  - pages/roadmap/followups.md
  - pages/decisions/0032-per-feature-write-permissions.md
reliability: medium
---

# Phase 2: the findings of Scout's audit of its own repository, lane by lane

> Every finding Scout's audit opened on this repository is fixed, or locked with a test and answered, inside pull request 120, and every lane's state is written here so the work resumes from this page and not from memory.

## Why now

Scout audited its own repository on 2026-11-08 and 2026-11-09 and opened 98 issues, 31 comments on pull request 120 and several review threads. The cause was that issue and comment writes ran under the grant alone; that is one of the lanes below. The owner's rule is that each finding is resolved inside the open pull request, with a test that proves a real defect or a test that locks the behaviour of a finding judged not to be one, and that the issue, the pull request comment and the review thread are answered as soon as the fix is pushed.

## Scope

- **In scope:** the 98 issues (#121 to #221), the review threads of pull request 120, the prototype lanes that share the branch (versions 3, 4, 5, 9 and 10 of [[pages/roadmap/prototype-migration]]).
- **Out of scope:** merging the pull request, a release, the screens of the prototype migration not listed here, the follow-ups the lanes recorded in [[pages/roadmap/followups]].

## Target outcome

Every issue carries its full answer (the commit, the tests, the limits stated plainly), every review thread is answered, the full test surface is green on the branch on a quiet machine, and a local AppImage built from the branch is with the owner.

## Status

- **As of 2026-11-10, end of day:** every lane is on the pull request branch and nothing is left uncommitted. The full test surface is green on the branch on a quiet machine, a local AppImage built from it passed the packaged smoke test, and each of the 124 findings the pull request set out to answer (#121 to #247) carries its answer on its issue.
- **Next milestone:** the owner merges the pull request; the repository is audited again only after the merge, so the audit reads the corrected code.
- **Not part of this pull request:** #248 to #330, opened by a second pass of the audit over `main` while this branch was open. Most repeat findings this branch already answers; they are read after the merge.

## What landed

| Lane | What | Findings |
|---|---|---|
| e2e seed | the write log seed of the loaded Approvals test is a tracked file | the red e2e job |
| shell, wizard, Approvals, Meetings, Models | versions 5, 4, 9, 10 and 3 of [[pages/roadmap/prototype-migration]] | none |
| adapters, audit, conversation memory | lifecycles, bots, attachments, read coverage, memory keys and trimming | #146 to #157, #173 to #181 |
| knowledge | snapshots, paths, failed and interrupted indexing, member lookup | #188 to #194 |
| control plane | the write gate, the action queue, setup and approvals | #158 to #170 |
| stream pump | error endings within the sink's limit, bounded terminal writes | #178, #179 |
| approval of GitHub writes, stop and clear | every issue, comment, reply and review waits for the owner; sent at most once | #154, #176 |
| quick-ask tests | teardown in a hook, the restart path in its own file | #197, #198 |
| vault backend | a keyring only, on Linux | #222 |
| audit verdict, Teams, Slack, discovery | `incomplete` when a file was not read; state and handler read in one step | #152, #153, #194 |
| sandbox command | the CI test line taken as written; a line that cannot run in a fresh snapshot not chosen | the review's "Tests failed (exit 2)" |
| harness | the git guard asks on every GitHub write; five documents and graders | #226, #229, #232, #240, #241, #246 |

Eleven findings of the second audit repeated ones already fixed (#224, #225, #228, #230, #231, #234, #236, #242 to #245) and were answered by pointing at the first. Eight were not taken here and are entries in [[pages/roadmap/followups]]: #223, #227, #233, #235, #237, #238, #239, #247.

## How a finding is handled

A finding is changed only with a check that fails before the change and passes after it. Three fixes that had first been accepted on reading the code (the discovery listing, the Teams start, the Slack session read) each gained an internal seam and a test that is red on the old code. A finding for which no such check can be written is answered as not established and nothing changes. A finding is called confirmed on its issue only after a test or a probe reproduced it.

## Decisions waiting for the owner

1. **Approving without the text.** An approval card for a comment or an issue shows the summary of the call and not the text. A bounded preview needs an optional field on the pending action in the control plane contracts. Proposed as its own lane, under [[pages/decisions/0032-per-feature-write-permissions]].
2. **Models, version 3.** The removal with the guard above, or the removal alone.
3. **Approvals, version 9.** The word "Approved" on the decision row of the artboard; the Material form field for the reason, recorded as not adopted.
4. **Wizard, version 4.** The introduction step is out of the artboard because it is not approved; the token field returns to hidden after every save.
5. **Meetings, version 10.** The notices after an export and a delete are in the artboard as a design the code has not reached.
6. **Shell, version 5.** The labels "Not started" and "Stopped unexpectedly"; the extended Latin range of the bundled font.
7. **Knowledge.** One re-index after an upgrade, so the wider member lookup reaches an index built before it, at the cost of one embedding run per project.
8. **Control plane.** After a revoke with an upload queued the publication stays approved and every later publish is refused; recorded in [[pages/roadmap/followups]].
9. **Stream pump.** No overall timeout is set in production; only the Teams cap of 120 seconds bounds a run.
10. **Verdicts.** One action per pull request and commit across Post, Approve and Request changes.

## Rules this phase added

- An answer is posted as soon as its fix is pushed, never in a batch at the end.
- The renderer suite times out at its 5000 ms default when the machine's load is above about 50, with no assertion failing; on a quiet machine the whole suite passes at that default. Gates of many lanes are not run at once, and a timeout under load is neither a defect nor a proof.
- A write a review calls redundant is removed only after every path that reaches it has been read.
- A lane that branched before another landed is tested again on the branch: two lanes green alone disagreed here in two source files, one test and one skill.

## Test build of 2026-11-10

The first build of the day, made from a throwaway branch (`test/build-2026-11-10`, never pushed) that holds the integration branch, versions 9 and 4, and the uncommitted code of `gp/bot-knowledge`, `gp/bot-cp`, `gp/bot-sink` and `gp/fix-stop`, with the three adaptations above. Not in it: version 3 and `gp/bot-partials`. The lanes that are not committed are in it without their last review; the build shows behaviour, it does not show they are ready.

On that exact tree, on a quiet machine and at default timeouts: the solution builds at 0 warnings; the 19 .NET test projects pass 4,769 tests with none skipped; the desktop passes typecheck, lint and format, 79 shared, 2,327 main, 68 preload and 3,657 renderer tests with no timeout, and 15 end-to-end tests; the smoke test of the packaged AppImage passes its 5 checks (fuses, the window opens, the packaged worker and control plane start, no startup error, a missing worker is reported). Version `0.3.0-test.202611100713`. The context reference check was not run on that branch, whose context folder pages were merged without care and are not part of the build.

## Final build of 2026-11-10

Built from the pull request branch itself. On that tree, on a quiet machine and at default timeouts: the solution builds at 0 warnings; the 19 .NET test projects pass 4,870 tests with none skipped; the hook and script self-tests pass; the desktop passes typecheck, lint and format, 79 shared, 2,362 main, 68 preload and 3,789 renderer tests with no timeout, and 15 end-to-end tests; the smoke test of the packaged AppImage passes its 5 checks. Version `0.3.0-test.202611101231`.
