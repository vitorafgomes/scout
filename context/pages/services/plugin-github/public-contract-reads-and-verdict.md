---
title: "Plugin.GitHub: Public contract, reads and the verdict"
version: "1.0"
date: 2026-11-21
updated: 2026-11-21
changes: "v1.0: the page as first written, split from the public contract"
page_type: service
status: active
description: "Read when a change touches get_file_contents by lines, search_code at a reference, or the decision the verdict follows."
source: ["src/Scout.Plugin.GitHub/", "tests/Scout.Plugin.GitHub.Tests/"]
reliability: high
---

# Plugin.GitHub: Public contract, reads and the verdict

> The reads and the verdict since 2026-11-21, beside [[pages/services/plugin-github/public-contract]].

- **The file read by lines.** `get_file_contents` answers a file in parts, whole lines from its `firstLine` (1 unless given) that fit 20,000 characters and at most its `lineCount` (0 for as many as fit), each part headed by its lines, the file's line count and the `firstLine` to call again with, or that the file ends there, with no line numbers added so a quote of the text stays exact; a single line longer than 20,000 is cut there and says so, a `firstLine` below 1 or a negative `lineCount` is refused before any request, and a `firstLine` past the end answers the file's line count (`src/Scout.Plugin.GitHub/GitHubPlugin.Reads.cs:671`, the same for the API read and the local snapshot; tests from `tests/Scout.Plugin.GitHub.Tests/GitHubPluginReadsTests.cs:535`, the same repository, reference and path at `:581`); [[pages/decisions/0071-the-file-read-by-lines-and-the-search-at-a-reference]]
- **`search_code` at a reference** (`src/Scout.Plugin.GitHub/GitHubPlugin.Reads.cs:377`): with `reference`, it searches Scout's local snapshot at that reference only and asks GitHub nothing; with none, the newest local snapshot, or GitHub's default branch when there is none. Its first line names what was searched. A reference with no snapshot answers that nothing was searched (`:428`); a snapshot that cannot be read says so. Tests: `tests/Scout.Plugin.GitHub.Tests/GitHubPluginReadsTests.cs:646`, `:674`. [[pages/decisions/0071-the-file-read-by-lines-and-the-search-at-a-reference]]
- **The verdict follows one decision.** `OfferFor` (`src/Scout.Plugin.GitHub/GitHubActionsPlugin.cs:330`) and the card's `WithOffer` (`src/Scout.Plugin.GitHub/GitHubPlugin.Findings.cs:59`) call `ReviewOffers.Decide`; `give_pull_request_verdict` refuses a verdict the review does not offer (`GitHubActionsPlugin.cs:318`) and a request for changes with no finding that needs it (`:315`). With no test run kept for the commit the decision is told that no test ran here (`:321`). Scout does not offer approval on its own. Tests: `tests/Scout.Plugin.GitHub.Tests/GitHubActionsPluginVerdictTests.cs:67`, `:480`. [[pages/decisions/0070-one-verdict-decision-and-no-approval-on-its-own]]
- **Evidence.** `present_review_findings` takes each finding's `evidence`, a quote of the new side of its file's patch or of its removed lines without the leading `-`. [[pages/decisions/0066-every-finding-carries-evidence-and-its-line-is-derived-by-code]]
