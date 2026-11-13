---
title: "Skills: A review or an audit (2026-11-10)"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Skills service page."
page_type: service
status: active
description: "Read when a change touches: A review or an audit (2026-11-10)."
source: ["src/Scout.Skills/", "tests/Scout.Skills.Tests/"]
reliability: high
---

## A review or an audit (2026-11-10)

A message that names a pull request ("PR 5", "PR #5", "pull request 5", "os PRs abertos", a `/pull/5` link, `owner/repo#5`) is decided by `LexicalSkillIndex.ReviewOrAudit` before any score is compared, on both selection paths; with embeddings it runs before the query is embedded, so a decided message costs no embeddings call. The rule: explicit words for the whole repository or project make it an audit; otherwise a request for a review or an analysis of a named pull request is a review, whatever else it asks to look for. In detail: an audit verb on the repository or project ("audita o repositório"), or a review, revision, analysis or audit verb followed within three words by "whole" or "entire" and the repository, project or codebase, or by "repositório" or "projeto" with "todo" or "inteiro", picks `code-audit`; otherwise a review, revision, analysis or audit verb picks `code-review`; and either way `code-audit` is not picked by its score, so a message that names a pull request without a review verb is scored among the other skills. A message that names no pull request is scored as before: "code review" alone and "faz o code review do acme/shop" still pick `code-review`, whose body then asks which pull request, or lists the open ones, and never starts an audit. The forced pick is a skill that is loaded and, on the words path, declares functions; a custom skills directory without it falls back to the scores.

The skill texts say the same: `code-audit`'s description says it is never for a pull request the user names, in its first sentence (the catalog summary) and in its "Not for" clause, and its body's first rule, before the audit job section, says a named pull request is a review: no audit, no issues, load `code-review`, unless the user also asks in so many words for the whole repository. `code-review`'s body says a review never opens an issue: its findings go on the pull request, as the cards and then the review the owner posts. The tests are `LexicalSkillIndexTests.A_Request_For_A_Code_Review_Of_A_Pull_Request_Is_A_Review_Whatever_Else_It_Asks_To_Look_For` (every form above with each thing to look for, in both languages), `A_Review_With_No_Pull_Request_Stays_A_Review_And_Explicit_Whole_Repository_Words_Win_Over_A_Named_Pull_Request`, `A_Message_That_Names_A_Pull_Request_Without_Asking_For_A_Review_Never_Picks_The_Audit`, `SkillSelectorTests.With_Embeddings_A_Code_Review_Of_A_Named_Pull_Request_Is_A_Review_Even_When_Its_Vector_Is_Nearest_The_Audit` and the two `BundledSkillsTests` on the texts.
