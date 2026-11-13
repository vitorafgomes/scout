---
title: "Skills: The review walks the owner's ten steps (2026-11-10)"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Skills service page."
page_type: service
status: active
description: "Read when a change touches: The review walks the owner's ten steps (2026-11-10)."
source: ["src/Scout.Skills/", "tests/Scout.Skills.Tests/"]
reliability: high
---

## The review walks the owner's ten steps (2026-11-10)

`src/Scout.Skills/skills/code-review.md` is written as the ten steps the owner gave on 2026-11-10, in order, each a `## N.` section ([[pages/architecture/code-review-flow]]): 1. context, where `get_pull_request` is read, the linked issue or story is read with `get_issue` (declared on the functions line, `:4`) and the summary says whether the change solves what it claims, and the earlier comments are read; 2. overview, which reads the files, the snapshot and the language skills and says once, in the summary, when the pull request is too large or mixes a refactor, a feature and a fix, with how to split it; 3. pipeline, the checks and the container test run before judging; 4 to 8, under "What to look for", correctness (empty collections, limits, time zones; idempotency, retries, duplicated and out-of-order messages in event-driven code), design (responsibilities and coupling; `search_code` for the same thing already existing, named in the finding), readability (magic numbers, a missing comment where the reason is not obvious, "would someone new understand this in six months"), tests (error paths, behaviour over implementation details) and security and performance (N+1 queries, needless calls, wrong use of async and await, allocations inside loops, only when the diff shows it); 9. feedback, on the code and never the person, at most ten findings on a `path:line` of the diff, a "Done well" part of at most three lines in the summary, praise never a finding or a posted comment, then the cards; 10. decision and follow-up, the posting, the verdict and the comments at the owner's request and approval, and a review asked again after new commits that checks each earlier finding against the new head (fixed, still there or changed), reports only new findings as new and replies on a thread only when the owner asks. Scout cannot mark a conversation resolved, and the skill says so. The body grew from 85 lines and 13,342 characters to 102 and 15,513. `tests/Scout.Skills.Tests/BundledSkillsTests.cs:457` holds one row per added item and `:466` the order of the steps and the kept rules.
