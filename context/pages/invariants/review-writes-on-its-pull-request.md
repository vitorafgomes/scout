---
title: "A review writes on its pull request, an audit opens issues"
version: "1.1"
date: 2026-11-10
changes: "v1.1 (2026-11-10): in a review the audit is kept when the message names the audit (stop, pause, continue, start, how is it going) as well as when it asks for the whole repository, so the owner keeps control of a running audit; citations of the router tests re-pointed."
page_type: invariant
status: active
description: "A turn that runs the code-review skill is offered writes on its pull request only and never a function that opens an issue or starts an audit; the code-audit skill may open issues and is never offered a write on a pull request; only code-audit, project-improvements and github-issues declare create_issue."
enforced_by:
  - "src/Scout.Skills/skills/code-review.md:4"
  - "src/Scout.Skills/skills/code-audit.md:4"
  - "src/Scout.Conversation/Routing/LlmStreamingRouter.cs:147"
  - "src/Scout.Conversation/Routing/LlmStreamingRouter.cs:213"
  - "src/Scout.Skills/SkillSelector.cs:245"
  - "src/Scout.Llm/ChatGateway.cs:197"
  - "src/Scout.Llm/ChatGateway.cs:264"
verified_by:
  - "tests/Scout.Skills.Tests/BundledSkillsTests.cs:396"
  - "tests/Scout.Skills.Tests/BundledSkillsTests.cs:405"
  - "tests/Scout.Skills.Tests/BundledSkillsTests.cs:414"
  - "tests/Scout.Skills.Tests/BundledSkillsTests.cs:424"
  - "tests/Scout.Conversation.Tests/Routing/LlmStreamingRouterFunctionScopeTests.cs:322"
  - "tests/Scout.Conversation.Tests/Routing/LlmStreamingRouterFunctionScopeTests.cs:337"
  - "tests/Scout.Conversation.Tests/Routing/LlmStreamingRouterFunctionScopeTests.cs:358"
  - "tests/Scout.Conversation.Tests/Routing/LlmStreamingRouterFunctionScopeTests.cs:370"
  - "tests/Scout.Host.Console.Tests/HostReviewTurnTests.cs:54"
  - "tests/Scout.Host.Console.Tests/HostReviewTurnTests.cs:111"
  - "tests/Scout.Host.Console.Tests/HostReviewTurnTests.cs:125"
source:
  - chat
  - pages/decisions/0054-functions-scoped-per-skill-and-job.md
  - src/Scout.Skills/skills/code-review.md
  - src/Scout.Skills/skills/code-audit.md
  - src/Scout.Conversation/Routing/LlmStreamingRouter.cs
reliability: high
---

# A review writes on its pull request, an audit opens issues

> A turn that runs `code-review` is never offered a function that opens an issue or starts an audit, and a turn that runs `code-audit` is never offered a write on a pull request.

## Why this exists

The owner's rule (2026-11-10): when a code review is requested it must always add comments on the pull request and never open issues; when asked to analyse a repository it may open issues if it finds something wrong. A review request that named a pull request had been picked as the audit, whose turn offers `create_issue` and `start_repository_audit`. The routing was fixed first ([[pages/history/skills]], 2026-11-10); this page holds the rule by structure, so a routing mistake or a later edit to a skill cannot break it.

## Scope

- **Applies to:** the bundled skills' `functions` lines, every chat turn the router composes, the per-turn kernel the gateway builds from them.
- **Does not cover:** an explicit request to open an issue, which is its own request and goes to `github-issues` as before; the desktop's `Issues to open` marker, which runs `code-audit` for issues the owner chose on the audit's cards, also in a conversation that was a review; an audit that finds a problem inside an open pull request, which is still a finding that may become an issue and writes nothing on the pull request.

## Where it's enforced

- `src/Scout.Skills/skills/code-review.md:4` - the review's functions line: the review, the verdict, `comment_on_pull_request` (which posts on the pull request's conversation, `issues/{n}/comments` on GitHub) and `reply_to_review_comment`; no `create_issue`, no audit function and, since 2026-11-10, no `comment_on_issue`, which accepted any number.
- `src/Scout.Skills/skills/code-audit.md:4` - the audit's functions line: `create_issue` and the audit job, no write on a pull request.
- `src/Scout.Conversation/Routing/LlmStreamingRouter.cs:147`, `:213` - in a turn whose selected or carried skills hold `code-review`, `code-audit` is dropped unless the message's own text names the audit (audit, auditoria, audita and their forms) or asks for the whole repository (`SkillSelector.AsksForAnAudit`, `src/Scout.Skills/SkillSelector.cs:245`; the whole-repository words are the ones that make a named pull request an audit); a turn left with no skill keeps the carried review. A follow-up whose words score the audit ("look for security bugs too", "e performance?") or a selection of both skills stays the review; "para a auditoria do acme/shop", "pause the audit", "continua a auditoria" run the audit with its controls. For a message that names a pull request the routing decides first and an audit word alone keeps it a review ([[pages/services/skills]]), so the two rules agree.
- `src/Scout.Llm/ChatGateway.cs:197`, `:264` - the per-turn kernel is built once from the request's functions before the call; `load_skill` (`src/Scout.Skills/SkillsPlugin.cs:37`) only returns a skill's text, so loading the audit's text in a review turn offers nothing more, and a call to `create_issue` there never reaches the write gate.

## How it's verified

- `tests/Scout.Skills.Tests/BundledSkillsTests.cs:396` - code-review declares every pull request write and none that opens an issue or starts an audit; `:405` code-audit declares `create_issue` and no pull request write; `:414` only code-audit, github-issues and project-improvements declare `create_issue`; `:424` the review comments through `comment_on_pull_request` and has no `comment_on_issue`.
- `tests/Scout.Conversation.Tests/Routing/LlmStreamingRouterFunctionScopeTests.cs:322` - a follow-up in a review conversation ("look for security bugs too", "procura problemas de performance também", "e performance?", "procura bugs também", "sim") stays the review; `:337` the review and the audit selected together run the review alone; `:358` a follow-up that names the audit (stop, pause, continue, start, "audita o acme/shop", "como está a auditoria?") or asks for the whole repository is the audit; `:370` the `Issues to open` marker still runs the audit.
- `tests/Scout.Host.Console.Tests/HostReviewTurnTests.cs:54` - through the composed host on the Ollama connector, a review turn whose model loads `code-audit` and then calls `create_issue` is offered the same review writes on every round and the write gate is never asked; `:111` in a review conversation a message that names the audit is offered `stop_repository_audit`, `pause_repository_audit` and `start_repository_audit`; `:125` a follow-up that does not name it stays the review.

## Known exceptions

- The `Issues to open` marker adds `code-audit` after the rule, so a review conversation in which the owner sends issues chosen on audit cards runs both skills for that turn.

## Related

- Services bound by this: [[pages/services/skills]], [[pages/services/conversation]]
- ADR that established the per-turn functions: [[pages/decisions/0054-functions-scoped-per-skill-and-job]]
- Similar invariants: [[pages/invariants/no-executive-writes]]

## Change protocol

If a future change must violate this invariant:

1. Draft a superseding [[pages/decisions/0054-functions-scoped-per-skill-and-job]] amendment or a new ADR explaining why.
2. Update this page: set `status: deprecated`, set `superseded_by:`.
3. Replace the enforcement code with the new rule the ADR establishes.
