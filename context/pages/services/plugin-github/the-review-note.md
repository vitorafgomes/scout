---
title: "Plugin.GitHub: The review note"
version: "1.1"
date: 2026-11-20
updated: 2026-11-20
changes: "v1.1 (2026-11-20): the digest covers the extras; a commenting review at a moved head is refused. v1.0: the composer, findings_json, the bounds, the marker, the preview and the purpose a review start hands over, moved here from the writes and the public contract."
page_type: service
status: active
description: "Read when a change touches: the review note; ReviewNote; findings_json; the note's bounds and marker; the note preview; PullRequestPurpose."
source: ["src/Scout.Plugin.GitHub/ReviewNote.cs", "src/Scout.Plugin.GitHub/GitHubActionsPlugin.cs", "src/Scout.Plugin.GitHub/GitHubVerdictRunner.cs", "src/Scout.Plugin.GitHub/GitHubActionsPlugin.Reviews.cs", "src/Scout.Plugin.GitHub/Abstractions/IPullRequestReviews.cs"]
reliability: high
---

# Plugin.GitHub: The review note

> What Scout writes on a pull request is composed by code in the worker, in the layout of the GitHub notes artboard, from the findings; the model never writes the body ([[pages/decisions/0063-what-scout-writes-on-a-pull-request-follows-the-templates]]). The writes themselves are on [[pages/services/plugin-github/the-writes]].

## The composer

`ReviewNote` (`src/Scout.Plugin.GitHub/ReviewNote.cs:35`) composes the review note and the comment on each finding's line, with no I/O. `submit_pull_request_review` and `give_pull_request_verdict` take `findings_json`, the array `present_review_findings` takes, and never a body (`src/Scout.Plugin.GitHub/GitHubActionsPlugin.cs:119`); a payload with a body and no findings is refused as unreadable.

## Bounds and marker

A note has at most 60,000 characters (`ReviewNote.cs:38`); a longer one first loses the checks' table, then the notes, and one that still does not fit is not sent. A comment on a line has at most 10,000 (`:41`). Every note ends with `<!-- scout-review {digest} -->` (`:92`), the digest of the verdict, the findings, the files read and changed and the notes (`:85`), the same content the control plane compares. A commenting review whose given commit is no longer the head is refused before anything is sent with the verdict's sentence for a head that moved, "The pull request changed since the review; review it again." (`src/Scout.Plugin.GitHub/GitHubActionsPlugin.cs:174`), and its preview answers the same; a review with no commit given is composed at the head. A verdict the signer already gave with that event on that commit is answered with its link only when its note carries the same marker (`src/Scout.Plugin.GitHub/GitHubActionsPlugin.cs:401`), so a different note at the same commit is sent and a lost answer never posts the same note twice.

A write GitHub accepts with an empty or unreadable answer is a failure saying it may already be there (`src/Scout.Plugin.GitHub/GitHubWriteClient.cs`); a success answer of another content type, such as an HTML page, takes the same path, as `GitHubWriteClientTests::A_Write_GitHub_Accepts_With_An_Unreadable_Answer_Says_It_May_Already_Be_There` shows with its `text/html` cases.

## The preview

`GitHubVerdictRunner` implements `IReviewNotePreview` (`src/Scout.Plugin.GitHub/GitHubVerdictRunner.cs:28`): `PreviewAsync` (`:67`) answers `{"body", "comments", "dropped"}` for a verdict as the card would queue it, with the send's reads and checks and nothing sent; a verdict it cannot read, or one the send would refuse, throws with the sentence of the refusal. The desktop reaches it through the worker's stdin ([[pages/services/adapter-local/public-contract]]).

## The purpose a review start hands over

`start_pull_request_review` hands the review job the pull request's `PullRequestPurpose` (`src/Scout.Plugin.GitHub/Abstractions/IPullRequestReviews.cs`): its title, its description and the issue its description names, read once (`src/Scout.Plugin.GitHub/GitHubActionsPlugin.Reviews.cs:73`), marked unread when GitHub fails or does not find it ([[pages/decisions/0064-the-review-batch-is-handed-the-purpose-the-rules-and-what-was-removed]]).
