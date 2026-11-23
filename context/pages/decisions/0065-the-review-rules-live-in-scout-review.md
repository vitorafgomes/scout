---
title: "ADR 0065 - The rules of a pull request review live in one project, Scout.Review"
version: "1.0"
date: 2026-11-21
updated: 2026-11-21
changes: "v1.0: the decision as first written"
page_type: adr
status: accepted
description: "Every rule that judges a review's findings, places them on the diff, reads the run's facts, decides the offer, plans the second reading and caps a card's notes lives in Scout.Review, a project with no reference and no I/O; the audit job and the GitHub plugin call it, and neither keeps a copy."
supersedes: ""
superseded_by: ""
source:
  - chat
  - src/Scout.Review/Scout.Review.csproj
  - src/Scout.Review/Finding.cs
  - src/Scout.Review/ReviewOffer.cs
  - src/Scout.Review/Contestation.cs
reliability: high
---

# ADR 0065 - The rules of a pull request review live in one project, Scout.Review

> A rule of the review is written once, in Scout.Review, and every caller uses that one; a second caller moves the function there, it never copies it.

## Status

Accepted on 2026-11-21, built at the owner's request that each review rule have one home. It shapes [[pages/decisions/0055-pull-request-review-as-a-job-in-batches]] and the records that follow it, whose texts stay as they were decided.

## The problem

The rules a review applies were spread over the audit job (`Scout.Audit`) and the GitHub plugin (`Scout.Plugin.GitHub`): the finding's shape and limits, the lines a comment may sit on, the verdict a review offers, the note's bounds. When the card and the note disagreed, the cause was two copies of one rule. The review also gained rules that both sides need: where a quote sits in a patch, the second reading's answer, the severity a finding ends with.

## The decision

- **One project.** `src/Scout.Review` holds the rules and nothing else. Its project file references no other project and no package (`src/Scout.Review/Scout.Review.csproj`), so it cannot read a file, call the network or reach the model: every type in it is pure and tested on its own in `tests/Scout.Review.Tests`.
- **What lives there.** The finding and its limits (`src/Scout.Review/Finding.cs`, `FindingLimits.cs`, `FindingValidation.cs`); the diff's lines and hunks (`DiffLines.cs`); the evidence match (`DiffQuote.cs`, [[pages/decisions/0066-every-finding-carries-evidence-and-its-line-is-derived-by-code]]); the parts of a large patch (`PatchParts.cs`, [[pages/decisions/0067-a-file-too-large-for-one-batch-is-read-in-parts]]); the focused readings and the reading order of a file (`ReviewReadings.cs`, [[pages/decisions/0068-focused-readings-only-on-code-and-test-batches]]); the pull request's description and its requests (`PullRequestDescription.cs`); the run's test facts (`TestRunFacts.cs`); the names a quote calls (`CalledNames.cs`); the second reading's request and its answer, the severity rule and the card's notes on it (`ContestRequest.cs`, `Contestation.cs`, [[pages/decisions/0069-the-second-reading-tries-to-refute-and-holds-only-with-a-scenario]]); the one offer of a review (`ReviewOffer.cs`, [[pages/decisions/0070-one-verdict-decision-and-no-approval-on-its-own]]).
- **Its callers.** `Scout.Audit` and `Scout.Plugin.GitHub` reference it and nothing else does. A rule that a second caller needs is moved into Scout.Review, never copied; the plugin's own copy of the diff lines was deleted when it moved.
- **What stays outside, and why.** The calls to GitHub, the model, the sandbox and the store stay in the projects that own them; Scout.Review is handed their results. The control plane still validates the shape of a findings card only (its fields and bounds), not the review's rules: it carries cards it did not compute, and checking them again there would make a third place a rule could drift. That remains open work ([[pages/roadmap/followups]]).

## Calls that leave the process

None. Scout.Review makes no call; the callers' calls are those of their own records.

## What follows

- **Gained:** a card and a note computed from one rule agree; a rule is tested once, with no host, no store and no model.
- **Paid:** the plugin and the audit depend on one more project, and a rule that needs I/O is split into a pure part here and a reading part in its caller.
- **To watch:** a new rule written inside a caller is the drift this decision ends; a reviewer checks that a review rule lands in Scout.Review.

## Rejected options

- **Keep each rule beside its first caller:** it had already led to two copies of the diff's lines and of the note's bounds.
- **Put the rules in the control plane's contracts:** the contracts carry data between processes; the rules would then load with every client of the control plane.

## How it is held

- The project file references nothing (`src/Scout.Review/Scout.Review.csproj`), so a call that leaves the process does not compile there.
- `tests/Scout.Review.Tests` tests each rule alone: the match (`tests/Scout.Review.Tests/QuoteMatchingTests.cs:32`), the offer (`tests/Scout.Review.Tests/ReviewOfferTests.cs:117`), the second reading's answer (`tests/Scout.Review.Tests/ContestationTests.cs:65`).

## Links

- Projects it shapes: [[pages/services/audit]], [[pages/services/plugin-github]]
- Decisions it leans on: [[pages/decisions/0055-pull-request-review-as-a-job-in-batches]], [[pages/decisions/0063-what-scout-writes-on-a-pull-request-follows-the-templates]]

## Amendments

None yet.
