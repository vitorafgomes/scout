---
title: "ADR 0063 - What Scout writes on a pull request follows the templates, composed by code"
version: "1.1"
date: 2026-11-20
updated: 2026-11-20
changes: "v1.1 (2026-11-20): the marker covers the extras; a commenting review at a moved head is refused. v1.0: the decision as first written"
page_type: adr
status: accepted
description: "The review note and the comment on a line follow the templates the GitHub notes artboard draws, composed by code in the worker for the card and for the chat; the model passes findings, never a body; a different verdict or different comments at a commit that already has one are sent, an identical request is not; the card previews the note through the worker's stdin."
supersedes: ""
superseded_by: ""
source:
  - chat
  - src/Scout.Plugin.GitHub/ReviewNote.cs
  - src/Scout.Plugin.GitHub/GitHubActionsPlugin.cs
  - src/Scout.Plugin.GitHub/GitHubVerdictRunner.cs
  - src/Scout.ControlPlane/WriteGate/PullRequestVerdictService.cs
  - src/Scout.Adapter.Local/LocalChatAdapter.cs
  - ui/packages/main/src/quick-ask/quick-ask.ts
  - ui/packages/renderer/src/app/core/review-findings.component.ts
  - context/prototypes/desktop/GitHubNotes.dc.html
reliability: high
---

# ADR 0063 - What Scout writes on a pull request follows the templates, composed by code

> The review note and each comment on a line are composed by code in the worker, in the layout of the GitHub notes artboard, for a click on the review card and for a verdict asked in chat alike; the model hands over findings and never writes a body. A second verdict, or other comments, at a commit that already has one is sent; the same request twice is not.

## Status

Accepted on 2026-11-20, by the owner's decision of 2026-11-19 that what Scout writes on GitHub follows the templates the prototype draws. It amends [[pages/decisions/0043-the-verdict-on-a-pull-request]] and [[pages/decisions/0059-approve-when-the-checks-pass]], and adds a dated note to [[pages/decisions/0032-per-feature-write-permissions]]; their texts stay as they were decided.

## The problem

The note a verdict posted was written twice: by the desktop for a click on the card, and by the model for a verdict asked in chat. The two drifted from each other and from the artboard (`context/prototypes/desktop/GitHubNotes.dc.html`). One pull request at one commit got one action from the card, so the owner could not change a verdict or post other comments after a first one, and a lost answer could not be told from a second, different request.

## The decision

- **One composer, in the worker.** `ReviewNote` (`src/Scout.Plugin.GitHub/ReviewNote.cs:35`) composes the note and the comment on each finding's line, with no I/O. The verdict, the commenting review and the chat functions all reach it through the plugin: `submit_pull_request_review` and `give_pull_request_verdict` take `findings_json`, the array `present_review_findings` takes (`src/Scout.Plugin.GitHub/GitHubActionsPlugin.cs:119`), and never a body. A payload with a body and no findings is refused as unreadable.
- **Bounds.** A note has at most 60,000 characters (`ReviewNote.cs:38`); a longer one first drops the checks' table, then the notes, and a note that still does not fit is not sent. A comment on a line has at most 10,000 (`ReviewNote.cs:41`).
- **The marker.** Every note ends with `<!-- scout-review {digest} -->` (`ReviewNote.cs:92`), the digest of the verdict, the findings, the files read and changed and the notes, the content the control plane compares. A commenting review whose given commit is no longer the head is refused before anything is sent, as a verdict is, and its preview answers the same refusal. A verdict whose marker is already on the commit is not sent again (`GitHubActionsPlugin.cs:401`), so an answer lost after the send never posts the note twice.
- **The same-commit rule.** A different verdict, or other content, at a commit that already has a verdict is its own action, approved and logged on its own. An identical request, the same commit, verdict and content digest, answers the action already there, unless it was rejected or failed (`src/Scout.ControlPlane/WriteGate/PullRequestVerdictService.cs:116`). The card names the verdict the control plane reports, keeps the Verdict group and Post offered after one was given, and says "Nothing new was sent: ..." when the click was the identical request.
- **The preview.** The card asks the worker for the note the click would post: the channel `scout:github.previewVerdict` (`ui/packages/shared/src/ipc-contract.ts:116`) carries the request to main, main writes a `note` request on the worker's stdin (`ui/packages/main/src/quick-ask/quick-ask.ts`), and the worker answers with `GitHubVerdictRunner.PreviewAsync` (`src/Scout.Plugin.GitHub/GitHubVerdictRunner.cs:67`), the same reads and composing as the send, with nothing sent. A refusal the send would meet is the preview's answer, shown as "The preview could not be composed: <sentence>"; giving the verdict stays enabled, since the send composes and checks again. The preview is rendered through the markdown pipe, which escapes raw HTML, and Angular's sanitiser.

### What of the artboard is built

The artboard's four states hold 100 rows; 53 are built and 47 are left out:

- **Built:** the heading and bar of each verdict, the commit line, what changed by area with the files not read, the tests Scout ran with what was left out, the checks on the commit with their table, the findings with their severity shortcode and the rule each breaks, how the review was made, and the comment on a line.
- **Left out, because no code measures them:** the requests asked and done, the reasons for the verdict, a summary of the change, the documentation and tests in the pull request, the tests for a person to run, impact and risk, release notes, suggested practices, the time taken, the AI source and model, the Scout version, the review time, and the inline suggestion, reproduction and related-lines blocks. A section nothing measured is left out, by the owner's decision of 2026-11-19, rather than drawn empty.

## Calls that leave the process

None new. The verdict and the commenting review are the declared, granted and approved writes they were ([[pages/decisions/0043-the-verdict-on-a-pull-request]]). The preview is a read the owner starts: it runs the reads the send runs (the pull request, its checks, the reviews on the commit) and writes nothing.

## Rejected options

- **A second composer in the desktop.** Two composers drift, as the two before this record did.
- **A read through the control plane for the preview.** The control plane does not reference the plugin, and the worker is its client, never its server; the worker's stdin is the existing path from the desktop to the plugin.
- **Matching a sent note by time.** A review posted around the same time is not the same note; the digest in the marker is.

## How it is held

- `tests/Scout.Plugin.GitHub.Tests/ReviewNoteTests.cs:64`, the whole note in the artboard's order.
- `tests/Scout.Plugin.GitHub.Tests/GitHubReviewNoteWritesTests.cs:117`, a note already on the commit is not sent again; `:241`, a body with no findings is refused; `:260`, the preview is the text the send would post.
- `tests/Scout.ControlPlane.Tests/WriteGate/PullRequestVerdictServiceTests.cs:127`, a different verdict is its own approval; `:155`, an identical request answers the action already there.
- `tests/Scout.Adapter.Local.Tests/LocalChatAdapterTests.cs:1000`, a note request whose previewer fails answers fail.
- `ui/packages/main/test/quick-ask.test.ts:3513`, `:3549`, `:3583` and `:4842`, the preview request, its failures and the windows allowed to ask it; `ui/packages/main/test/ipc-github-verdict.test.ts:362`, the status carries the verdict the control plane reports.
- `ui/packages/renderer/src/app/core/review-findings.component.spec.ts:760`, `:2426`, `:2461` and `:2566`: the preview is the worker's note, the card stays open after a verdict, an identical request sends nothing new, and markup in a finding never reaches the DOM as markup.

## Links

[[pages/decisions/0043-the-verdict-on-a-pull-request]], [[pages/decisions/0059-approve-when-the-checks-pass]], [[pages/decisions/0032-per-feature-write-permissions]], [[pages/services/plugin-github/the-writes]], [[pages/services/plugin-github/the-review-note]], [[pages/services/desktop-github/verdict-from-the-review-card]].
