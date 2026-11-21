---
title: "Audit: Public contract, review quality"
version: "1.2"
date: 2026-11-21
updated: 2026-11-21
changes: "v1.2 (2026-11-21): the noted outcome and the check of a cited place (ADR 0069 amended). v1.1 (2026-11-21): the window and the review turn bound (ADR 0074). v1.0: the page as first written, split from the public contract"
page_type: service
status: active
description: "Read when a change touches a review finding's evidence, the parts of a large file, the focused readings, the second reading or the turn table."
source: ["src/Scout.Audit/", "tests/Scout.Audit.Tests/"]
reliability: high
---

# Audit: Public contract, review quality

> What the review job offers since the review quality work of 2026-11-21, beside [[pages/services/audit/public-contract]].

- **Evidence.** Each review finding carries `evidence`, a quote of its file's patch; `AuditBatchRunner.Place` derives its line with `DiffQuote.Locate` (`src/Scout.Audit/AuditBatchRunner.cs:400`), whose order of steps forgives whitespace and a quote that is part of one line and never guesses a quote found more than once. A finding it cannot place is asked for once more per reading, each named by id, title and problem, with the evidence rule repeated (`:391`); an extra request that does not complete keeps the first answer's placed findings. [[pages/decisions/0066-every-finding-carries-evidence-and-its-line-is-derived-by-code]]
- **Parts.** A file too large for one batch is planned as at most 10 part batches with its reading-order group, parts in order (`src/Scout.Audit/RepositoryAudits.Reviews.cs:290`). [[pages/decisions/0067-a-file-too-large-for-one-batch-is-read-in-parts]]
- **Focused readings.** The removed and requests readings run only on batches with code or test files, and their findings join the general reading's (`AuditBatchRunner.cs:158`). [[pages/decisions/0068-focused-readings-only-on-code-and-test-batches]]
- **The second reading** reads at the head with `get_file_contents`, `search_code` and `list_repository_files` held to the job's repository (`:759`, `:771`), tries to refute, holds only with a scenario and sets the severity by rule; each group has the batch turn's timeout inside 30 minutes for the whole reading (`src/Scout.Audit/AuditWorker.Reviews.cs:610`). [[pages/decisions/0069-the-second-reading-tries-to-refute-and-holds-only-with-a-scenario]]
- **Noted, and the cited place.** A finding true in the code and recorded by the project as later work is answered `noted`: kept as a nit titled "Noted: ", its why ending with "Recorded by the project: " and the record, counted in one card note, never a reason to request changes; refuted stays for a false claim or the documented design. A refutation or a note citing a file or line the snapshot does not hold is uncertain (`src/Scout.Review/Contestation.cs:356`, [[pages/decisions/0069-the-second-reading-tries-to-refute-and-holds-only-with-a-scenario]], amended 2026-11-21).
- **The turn table.** `IAuditJobStore.AddTurnAsync` keeps one row of numbers per model turn in `audit_turn`, `TurnTotalsAsync` totals them per reading (`src/Scout.Audit/SqliteAuditStore.cs:129`, `:156`) and `StampRunAsync` writes the Scout version and the `provider/model` names on the job row (`:173`); the card's "Scout asked the model N times for this review." and its focused note are read from the totals (`AuditWorker.Reviews.cs:476`). [[pages/decisions/0073-the-numbers-of-each-model-turn-are-kept]]
- **The review's own tests** run through `run_pull_request_tests` on the shared kernel, so the write gate's "Local container" grant applies; a cancellation that is not the review's own answers that the tests could not be run.

- **The window and the turn's bound.** A review keeps at most `Scout:Audit:ReviewParallelBatches` batches in flight and starts the next pending one as one ends (`src/Scout.Audit/AuditWorker.Reviews.cs:326`); a review batch turn has `Scout:Audit:ReviewTurnTimeout`, 3 minutes (`src/Scout.Audit/AuditOptions.cs:40`), while audits and the second reading keep `TurnTimeout`; each turn row keeps `last_call_ms`. [[pages/decisions/0074-a-review-runs-its-batches-through-a-window]]
