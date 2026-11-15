---
title: "ADR 0057 - The owner's mention of the Scout bot starts the review by itself"
version: "1.0"
date: 2026-11-15
updated: 2026-11-15
changes: "v1.0: the decision as first written"
page_type: adr
status: accepted
description: "When the owner mentions the Scout bot on a pull request, the desktop asks the chat for its review by itself, once per mention, one at a time and only while the chat is idle, and the bell shows it in progress; anyone else's mention and an old one keep Review it, a mention whose pull request was merged, closed or approved by the bot loses it, and nothing is posted without the owner's approval."
supersedes: ""
superseded_by: ""
source:
  - chat
  - ui/packages/main/src/notifications/mention-review.ts
  - ui/packages/main/src/quick-ask/quick-ask.ts
  - ui/packages/main/src/main.ts
  - ui/packages/shared/src/review-question.ts
  - ui/packages/main/src/notifications/github-activity.ts
reliability: high
---

# ADR 0057 - The owner's mention of the Scout bot starts the review by itself

> When the owner mentions the Scout bot on a pull request, the desktop asks the chat for its review by itself, once per mention, one at a time and only while the chat is idle, and the bell shows it in progress; anyone else's mention and an old one keep Review it, a mention whose pull request was merged, closed or approved by the bot loses it, and nothing is posted without the owner's approval.

## Status

Accepted on 2026-11-15 by the owner, who asked that the review start as soon as the bot is mentioned on a pull request. It amends [[pages/decisions/0045-theme-and-notification-center]] v1.4, where a mention only made a bell entry whose Review it asked the chat on the owner's click.

## The problem

The owner mentioned the bot on a pull request and nothing started: the bell showed the mention, and the review waited for a click on Review it. The owner wants the mention itself to be the request.

## The decision

- **The owner's fresh mention asks by itself.** When the poll records a mention whose author is the owner's own GitHub login, written within the last hour, on a pull request, main asks the chat the question Review it asks, "Review the pull request <url>" (`ui/packages/main/src/notifications/mention-review.ts:50`, `ui/packages/shared/src/review-question.ts:5`). Every mention on a pull request counts; the text is not interpreted. A login that cannot be read leaves the mention with Review it.
- **Once per mention.** A mention that was asked is recorded with the detail "Review started by the mention. The answer is in the chat." and without Review it; an entry that carries that detail is never asked again, across an edit of the comment and a restart of Scout. A waiting mention the owner asked about meanwhile, with Review it or by hand, is marked the same way and asked nothing (`askedSince`, `ui/packages/main/src/quick-ask/quick-ask.ts:1846`).
- **One at a time, never over the owner.** The question is asked only while the worker runs and no turn is running (`askWhenIdle`, `ui/packages/main/src/quick-ask/quick-ask.ts:1826`), so it never stops a turn the owner started; a question the worker refuses leaves no turn in the chat and waits for the retry. A mention that cannot be asked waits: main tries again when the chat goes idle (`ui/packages/main/src/main.ts:1142`) and every minute, and gives the mention back to Review it once it is an hour old.
- **In progress in the bell.** While the review's turn runs the bell shows "Reviewing <owner>/<repo>#<n>", "Started by your mention of the Scout bot", under In progress with the sweeping bar. When the turn completes it reads "Review of <owner>/<repo>#<n> is ready", "The answer is in the chat." (`ui/packages/main/src/notifications/mention-review.ts:131`); when the turn stopped or failed, "Review of <owner>/<repo>#<n> did not finish", "It stopped before the answer. Review it asks again.", and the mention gets Review it back (`ui/packages/main/src/notifications/mention-review.ts:152`). A review running when Scout closes comes back as not finished, as every running entry but an audit's does.
- **A pull request that needs no review loses Review it.** After each read the poll reads the pull request of every mention the bell keeps that still offers Review it, at most 20 pull requests a read (`settle`, `ui/packages/main/src/notifications/github-activity.ts:258`). A merged one reads "Merged. Nothing to review.", a closed one "Closed. Nothing to review.", and an open one the Scout bot approved at its head commit "Approved by the Scout bot at this commit."; each loses Review it and is asked nothing by itself. An approval by anyone else, or at an older commit, changes nothing. A rate limit ends the settling and the next read waits what GitHub asks; any other error status leaves that pull request as it is.
- **Everyone else keeps Review it.** A mention by another login, one older than an hour, and one with no pull request link are recorded as before.
- **Disconnect ends it.** When GitHub is disconnected the waiting mentions are dropped with the GitHub entries, and none is asked (`ui/packages/main/src/notifications/mention-review.ts:174`, `ui/packages/main/src/main.ts:618`).
- **Nothing is posted from here.** The review is the chat turn the click already started; its cards, its verdict and every write stay the approved actions of [[pages/decisions/0043-the-verdict-on-a-pull-request]] and [[pages/decisions/0032-per-feature-write-permissions]]. Where the owner granted the GitHub feature's `Local container` row, that turn may run the pull request's tests in the container, as the same question typed or clicked already could.

## Calls that leave the process

- `GET https://api.github.com/repos/<owner>/<repo>/pulls/<n>` and `GET .../pulls/<n>/reviews?per_page=100`, with the owner's token, read: the state of a kept mention's pull request and the bot's approval. Declared on the GitHub feature's `Notifications` row (`src/Scout.ControlPlane/Features/FeatureCatalog.cs:86`).
- The question goes to the worker over the same stdin line as a typed one; the reads the review makes are the ones the GitHub feature already declares.

## What follows

- **Gained:** the owner asks for a review from GitHub and finds it prepared in the chat.
- **Paid:** a review now spends the AI Source without a click in Scout; the bounds are one per mention, the owner's login and one hour.
- **Paid:** the review is a chat turn, so a question the owner asks while it runs stops it; the bell says it did not finish and Review it asks again.
- **Paid:** up to 40 more GitHub reads every five minutes while mentions with Review it are kept.
- **To watch:** the poll reads every five minutes and only while Scout is open; a mention made while it was closed for more than an hour keeps Review it.

## Rejected options

- **Asking over a running turn:** a new question stops the running one; the owner's own turn would be lost.
- **Letting the model decide whether a mention asks for a review:** one more model call per mention, and wrong both ways; a mention on a pull request is the request.
- **Any login:** the repository may be public, and each review spends the owner's AI Source; a list of GitHub logins the owner allows is a later change.
- **A webhook:** the App is created with an inactive webhook and the desktop has no public address.

## How it is held

- `ui/packages/main/test/notifications-mention-review.test.ts:79`: the owner's fresh mention asks, its entry says it started and the review shows in progress; `:98`, once per mention, across an edit and a restart; `:115`, another login keeps Review it; `:125`, an old mention, one with no pull request link and GitHub disconnected; `:137`, it waits for the chat and asks one at a time, the second review still running after the first ends; `:162`, a refused question is asked again only on the retry; `:190`, ready when the turn completes; `:209`, not finished and Review it back when it stopped; `:228`, another turn's end changes nothing; `:238`, it asks again after a refusal and gives up at an hour; `:254`, nothing is asked after Disconnect; `:268`, a login that cannot be read; `:277`, a mention the owner already asked about; `:287`, a mention that lost Review it; `:300`, every other entry is recorded as it came.
- `ui/packages/main/test/quick-ask.test.ts:72`: `askWhenIdle` asks when no turn runs and answers the turn's id, and asks nothing while one runs or the worker is not running; `:97`, a line the worker refuses answers null and leaves no turn, and `askedSince` finds a question by its text and time.
- `ui/packages/main/test/github-activity.test.ts:132`: one read per pull request and a merged one; `:152`, a closed one; `:164`, the bot's approval at the head commit, and the approvals that do not count; `:201`, no bot; `:214`, a 502, a 404 and a timeout on the pull request read; `:230`, a 502, a timeout and a 401 on the reviews read; `:251`, a rate limit waits what GitHub asks; `:266`, a 404 leaves that pull request and the next is read; `:284`, a 401 stops the poll; `:293`, at most 20 pull requests a read.
- `tests/Scout.ControlPlane.Tests/Features/FeatureCatalogTests.cs:67`: the `Notifications` row names the reads.

## Links

- Projects it shapes: [[pages/services/desktop-github/mentions-of-the-scout-bot]]
- Invariants it leans on: [[pages/invariants/no-executive-writes]]
- Decisions it amends: [[pages/decisions/0045-theme-and-notification-center]]
- What comes next: the review as a job, [[pages/decisions/0055-pull-request-review-as-a-job-in-batches]], so it no longer shares the chat's one turn.

## Amendments

None yet.
