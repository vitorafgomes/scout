---
title: "Review: history"
version: "1.0"
date: 2026-11-21
updated: 2026-11-21
changes: "v1.0: the page as first written"
page_type: reference
status: active
description: "What changed in Scout.Review, the project that holds the rules of a pull request review, newest first."
source: ["src/Scout.Review/", "tests/Scout.Review.Tests/"]
reliability: high
---

# Review: history

> The dated changes of [[pages/services/review]], newest first.

## 2026-11-21 - The project as built

- **Was:** the review's rules lived in the audit job and the GitHub plugin, with a copy of the diff's lines in each.
- **Is:** one project, `src/Scout.Review`, holds the finding, the diff's lines, the evidence match with its order of steps ([[pages/decisions/0066-every-finding-carries-evidence-and-its-line-is-derived-by-code]]), the parts of a large patch ([[pages/decisions/0067-a-file-too-large-for-one-batch-is-read-in-parts]]), the focused readings and the reading order ([[pages/decisions/0068-focused-readings-only-on-code-and-test-batches]]), the second reading's request, answer and severity rule ([[pages/decisions/0069-the-second-reading-tries-to-refute-and-holds-only-with-a-scenario]]) and the one offer ([[pages/decisions/0070-one-verdict-decision-and-no-approval-on-its-own]]); [[pages/decisions/0065-the-review-rules-live-in-scout-review]].
- **Since the first build, the same day:** a documentation folder counts only at the repository's root; a removed line whose own text starts with `--` is a removal; an answer entry decides only the finding whose id it names exactly; a reason or a scenario over 300 characters is cut on a word and kept; the code around a line not in the file is empty; the description's heading and table patterns are linear.
- **Tests changed on purpose:** `DiffQuoteTests` (a quote without its indentation now matches; lines not next to each other sit on their first line of code when it is found once alone), `ContestationTests` (holds needs a scenario; a long reason is kept, cut), `ReviewReadingsTests` (the readings take paths with their patches).
