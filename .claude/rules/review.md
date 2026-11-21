---
paths:
  - "src/Scout.Review/**"
  - "tests/Scout.Review.Tests/**"
---

# Review

Owns the rules of a pull request review as pure code: the finding and its limits, the diff's lines, the evidence match, the parts of a large patch, the focused readings, the second reading's request and answer, the severity rule and the one offer. Does not own any call: the audit job and the GitHub plugin read the pull request, the files and the model and hand the results here.

## Rules this project must keep

- **One home for the review's rules.** Here: a rule a second caller needs moves into this project, never copied, and nothing else references it but Scout.Audit and Scout.Plugin.GitHub.
- **No reference and no I/O.** Here: the project file references no project and no package, so nothing here reads a file, calls the network or reaches the model.
- **Text from a pull request or a model is data.** Here: every pattern that runs on it is linear or bounded by a match timeout.
- **A failure of the second reading never weakens a finding.** Here: an answer that cannot be read leaves its findings uncertain at worst, never refuted.

The full statement of each, with the code and tests that hold it: `context/pages/services/review.md`.

## Pages that govern this project

Read one when your change touches what its title names.

- [[pages/decisions/0065-the-review-rules-live-in-scout-review|ADR 0065 - The rules of a pull request review live in one project, Scout.Review]] - why this project exists and what stays outside it
- [[pages/decisions/0066-every-finding-carries-evidence-and-its-line-is-derived-by-code|ADR 0066 - Every finding carries evidence, and its line is derived by code]] - `DiffQuote`, `DiffLines`
- [[pages/decisions/0067-a-file-too-large-for-one-batch-is-read-in-parts|ADR 0067 - A file too large for one batch is read in parts]] - `PatchParts`
- [[pages/decisions/0068-focused-readings-only-on-code-and-test-batches|ADR 0068 - Two focused readings, run only on batches with code or tests]] - `ReviewReadings`
- [[pages/decisions/0069-the-second-reading-tries-to-refute-and-holds-only-with-a-scenario|ADR 0069 - The second reading tries to refute, and holds a finding only with a scenario]] - `ContestRequest`, `Contestation`, `SeverityRule`
- [[pages/decisions/0070-one-verdict-decision-and-no-approval-on-its-own|ADR 0070 - One decision gives a review's verdict, and Scout does not offer approval on its own]] - `ReviewOffers`

## What to read for a change

| If the change touches | Read |
|---|---|
| Any type of this project; its tests | `context/pages/services/review.md` |
| What changed and when | `context/pages/history/review.md` |
