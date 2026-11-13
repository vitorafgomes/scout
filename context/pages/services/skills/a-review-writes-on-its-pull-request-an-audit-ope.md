---
title: "Skills: A review writes on its pull request, an audit opens issues (2026-11-10)"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Skills service page."
page_type: service
status: active
description: "Read when a change touches: A review writes on its pull request, an audit opens issues (2026-11-10)."
source: ["src/Scout.Skills/", "tests/Scout.Skills.Tests/"]
reliability: high
---

## A review writes on its pull request, an audit opens issues (2026-11-10)

The owner's rule, that a requested code review always comments on the pull request and never opens an issue while an analysis of a repository may open issues, is held by structure as well as by the routing above ([[pages/invariants/review-writes-on-its-pull-request]]). `code-review`'s functions line (`src/Scout.Skills/skills/code-review.md:4`) declares the review, the verdict, `comment_on_pull_request` and `reply_to_review_comment`, and no function that opens an issue or starts an audit; `comment_on_issue` left it on 2026-11-10, because `comment_on_pull_request` already posts on the pull request's conversation (GitHub's `issues/{n}/comments`, `tests/Scout.Host.Console.Tests/GitHubActionExecutorTests.cs:197`) and `comment_on_issue` accepted any number. `code-audit`'s line declares no write on a pull request. Only `code-audit`, `github-issues` and `project-improvements` declare `create_issue`. `tests/Scout.Skills.Tests/BundledSkillsTests.cs:396`, `:405`, `:414` and `:424` fail the build on a skill edit that mixes them. In a conversation that is a review, a later turn whose words pick `code-audit` stays the review unless its own text names the audit (audit, auditoria, audita and their forms, so "para a auditoria", "pause the audit" and "continua a auditoria" keep the owner's control of a running audit) or asks for the whole repository, through `SkillSelector.AsksForAnAudit` (`src/Scout.Skills/SkillSelector.cs:245`), which the router calls ([[pages/services/conversation]]). For a message that names a pull request, `ReviewOrAudit` above still decides first and an audit word alone keeps it a review; only the whole-repository words turn it into an audit there. `load_skill` never widens a turn: loading the audit's text in a review turn offers nothing more and a `create_issue` call there never reaches the write gate (`tests/Scout.Host.Console.Tests/HostReviewTurnTests.cs:54`).
