---
title: "Skills: Four review criteria (2026-11-11)"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Skills service page."
page_type: service
status: active
description: "Read when a change touches: Four review criteria (2026-11-11)."
source: ["src/Scout.Skills/", "tests/Scout.Skills.Tests/"]
reliability: high
---

## Four review criteria (2026-11-11)

An audit Scout ran over a repository answered 83 findings, of which 17 repeated an open issue and 11 reported behaviour the project had recorded as deliberate, while two real defects of one shape, a validation that could never run because the line before it already threw for that input, fit no rule of either skill. Both skills now carry four criteria, each one or two sentences appended to the line that already talks about the subject, so no line of either file moved:

- **A guard that cannot fire, and an error reported as success.** A check placed after a line that already throws or returns for the input it guards, or testing a condition the surrounding code makes impossible, is a finding that names the input that should reach the guard and does not; a failed operation that answers as if it worked is a finding too. In `code-review` under 4. Functional correctness (`src/Scout.Skills/skills/code-review.md:37`), in `code-audit` in the Bugs dimension (`src/Scout.Skills/skills/code-audit.md:35`).
- **What the project records as intended is not a finding.** Next to "Not findings" in `code-review` (`src/Scout.Skills/skills/code-review.md:33`) and to the documented limitations in `code-audit`'s "How to answer" (`src/Scout.Skills/skills/code-audit.md:44`): a decision record, a note in the conventions or invariants that says it is on purpose, or a test whose name says the behaviour is intended; the audit puts it in `known_json` with the limitations.
- **What already has an open issue is not reported again** (audit only, same line): when `list_issues` is available the audit calls it once before reporting, and a finding an open issue already describes goes in `known_json` with the issue number. `GitHub.list_issues` (`src/Scout.Plugin.GitHub/GitHubPlugin.cs:86`), an existing read, joined `code-audit`'s functions line (`src/Scout.Skills/skills/code-audit.md:4`); no write was added. The audit job's batches read this sentence but are not offered the function (`src/Scout.Audit/AuditBatchRunner.cs:41`), which is why it is conditional; [[pages/roadmap/followups]] carries the gap.
- **Signal over volume.** Where each skill states its cap, unchanged (ten for the review, `src/Scout.Skills/skills/code-review.md:57`; 50 for the audit, `src/Scout.Skills/skills/code-audit.md:52`): a finding not confirmed by reading the cited line is not reported, and when the code is sound the answer is no findings, said plainly, never a list padded with tastes.

`code-review` also rates two failure modes: defensive work that buys nothing, a guard for a case the types or the data model already prevent, is a nit at most (`src/Scout.Skills/skills/code-review.md:37`); an optimisation that removes a guarantee nobody named (a filter widened, a re-read skipped, a transactional value cached) is a blocking finding when the diff shows the guarantee was relied on (`:53`). The tests assert each phrase inside the section it belongs to: `tests/Scout.Skills.Tests/BundledSkillsTests.cs:492` (the guard, both skills), `:505` (the error reported as success), `:513` and `:523` (the intended behaviour, and `known_json` in the audit), `:530` (the open issues and the functions line), `:542` (the cited line and no findings, with the cap) and `:552` (the review's two failure modes).
