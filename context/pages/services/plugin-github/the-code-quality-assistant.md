---
title: "Plugin.GitHub: The code quality assistant (2026-11-06)"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Plugin.GitHub service page."
page_type: service
status: active
description: "Read when a change touches: The code quality assistant (2026-11-06)."
source: ["src/Scout.Plugin.GitHub/", "tests/Scout.Plugin.GitHub.Tests/"]
reliability: high
---

## The code quality assistant (2026-11-06)

Three bundled skills direct the model over these reads, each taking the snapshot above first when the write is granted ([[pages/services/skills]]): `code-audit` (`src/Scout.Skills/skills/code-audit.md`) audits a whole repository with no pull request, learning its conventions first (the tree, the manifests, the lint and CI configuration, a sample per layer, `search_code` to confirm each rule) and then one dimension at a time (bugs, security, conventions, performance, tests, documentation), every finding anchored to `path:line` and to the profile line it departs from; `code-review` (`src/Scout.Skills/skills/code-review.md`) reads the pull request, the existing comments, the diffs left out and the surrounding code, loads the language skill for each extension and answers findings anchored to `path:line`; `project-improvements` (`src/Scout.Skills/skills/project-improvements.md`) reads the project through the Knowledge index when it is indexed or through `get_repository`, `get_file_contents` and the issues otherwise, and answers a prioritised what, why, how, where list. Posting the review and opening the issues go through `submit_pull_request_review` and `create_issue` above; both skills tell the model to call them only when the user asks; since [[pages/decisions/0040-review-findings-as-cards-in-chat]] the desktop's Post on the findings cards is that ask, a message carrying the chosen comments after the `Review to post` marker, which until 2026-11-09 `code-review` posted unchanged in one call; since then Post is the owner's approval of the review and goes to Scout Desktop as an approved action, never through the model, and a review asked for in chat is queued for the owner; since its v1.2 `code-audit` hands its findings to `present_audit_findings` and a message carrying the chosen issues after the `Issues to open` marker is the ask to open them, one `create_issue` per issue, unchanged, without labels, each queued for the owner's approval since 2026-11-09 and answered as queued, never with a URL. Since 2026-11-07 `code-review` reads `get_pull_request_checks` before judging, runs `run_pull_request_tests` when no check runs the tests or the user asks, passes the commit, the checks and the run to `present_review_findings`, calls `give_pull_request_verdict` only when the user asks for a verdict in chat (queued for the owner's approval), and the comment functions only when the user asks for that comment; the card's verdict buttons never go through chat.
