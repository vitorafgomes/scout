---
title: "ADR 0070 - One decision gives a review's verdict, and Scout does not offer approval on its own"
version: "1.0"
date: 2026-11-21
updated: 2026-11-21
changes: "v1.0: the decision as first written"
page_type: adr
status: accepted
description: "What a review offers the owner is decided by one function in Scout.Review, used by the card, the note's preview and the gate before a verdict is sent; it offers request changes for a finding that needs fixing and a comment otherwise, never approve nor wait for now, and the gate refuses a verdict the review does not offer; the approval code path stays for a later flag."
supersedes: ""
superseded_by: ""
source:
  - chat
  - src/Scout.Review/ReviewOffer.cs
  - src/Scout.Plugin.GitHub/GitHubActionsPlugin.cs
  - src/Scout.Plugin.GitHub/GitHubPlugin.Findings.cs
  - src/Scout.ControlPlane/Features/FeatureCatalog.cs
reliability: high
---

# ADR 0070 - One decision gives a review's verdict, and Scout does not offer approval on its own

> The card offers one action, decided by code. A person approves a pull request on GitHub; Scout requests changes or comments.

## Status

Accepted on 2026-11-21, by the owner's decision that Scout stops offering approval on its own until its first reading is measured reliable. It amends [[pages/decisions/0043-the-verdict-on-a-pull-request]], [[pages/decisions/0058-an-approval-needs-every-check-passed]] and [[pages/decisions/0059-approve-when-the-checks-pass]], whose texts stay as decided; each carries a dated amendment that names this record.

## The problem

The verdict was decided in more than one place: the worker's card, the plugin's preview and the gate before a send each had their own reading of findings, tests and checks, and they could disagree. Measured on real pull requests, the first reading missed most of what a careful review finds, so an approval on its own judgement could not be trusted.

## The decision

- **One decision.** `ReviewOffers.Decide` (`src/Scout.Review/ReviewOffer.cs:61`) takes the kept findings, the files read and changed, the run's test facts and the head commit's checks, and answers one offer: a verdict, its reason in one sentence and whether sending the findings as a comment instead is allowed. The card, the note's preview (`WithOffer`, `src/Scout.Plugin.GitHub/GitHubPlugin.Findings.cs:59`) and the gate (`OfferFor`, `src/Scout.Plugin.GitHub/GitHubActionsPlugin.cs:330`) all call it.
- **No approval on its own.** A confirmed finding that needs fixing gives request changes; anything else gives comment, whose reason ends "Scout does not approve on its own, so a person approves the pull request on GitHub." (`ReviewOffer.cs:170`). The constants for approve and wait stay, marked reserved: the card still draws those offers when given one, and the approval returns later under the owner's flag.
- **The gate refuses a verdict the review does not offer.** An approval is refused with "Scout approves only when the review offers approval." and the offer's reason (`GitHubActionsPlugin.cs:318`); a request for changes with no finding that needs one is refused with its own sentence (`:315`). The other refusals of [[pages/decisions/0058-an-approval-needs-every-check-passed]] stay in front of it.
- **No test run kept.** When no test run is kept for the commit, the decision is told no test ran here (`:321`), so its reason never says the tests passed: a clean review reads "…and every check passed, which covers the tests, since no test ran here".
- **What the owner sees.** The card shows the one action and its reason; the GitHub feature's declaration says Scout does not approve on its own (`src/Scout.ControlPlane/Features/FeatureCatalog.cs:107`); the verdict function's description tells the model the same. The approval and the waiting approval of [[pages/decisions/0059-approve-when-the-checks-pass]] stay in the code, unreachable while the decision never offers them.

## Calls that leave the process

Unchanged: the verdict is the same declared, granted and per-action approved write of the GitHub feature; this decision only narrows what it is offered.

## What follows

- **Gained:** the card, the preview and the gate agree; nothing is approved on a reading that misses problems.
- **Paid:** a clean pull request gets a comment, and a person approves it on GitHub.
- **To watch:** the approval returns only with the owner's flag, and then needs its own measured threshold.

## Rejected options

- **Keep approval with stricter conditions:** the first reading's misses are not visible in its conditions.
- **Remove the approval code:** it returns later; it stays tested and unreachable.

## How it is held

- `tests/Scout.Review.Tests/ReviewOfferTests.cs:117` (no review is offered approval on its own).
- `tests/Scout.Plugin.GitHub.Tests/GitHubActionsPluginVerdictTests.cs:67` (an approval is refused before any write), `:480` (with no test run kept, the reason never says the tests passed).
- `tests/Scout.ControlPlane.Tests/Features/FeatureCatalogTests.cs:379` (no declaration says Scout approves on its own or names the removed Post button).

## Links

- Projects it shapes: [[pages/services/review]], [[pages/services/plugin-github]], [[pages/services/desktop-ui]]
- Decisions it amends: [[pages/decisions/0043-the-verdict-on-a-pull-request]], [[pages/decisions/0058-an-approval-needs-every-check-passed]], [[pages/decisions/0059-approve-when-the-checks-pass]]

## Amendments

None yet.
