---
title: "Skills: The skills that act (2026-11-08)"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Skills service page."
page_type: service
status: active
description: "Read when a change touches: The skills that act (2026-11-08)."
source: ["src/Scout.Skills/", "tests/Scout.Skills.Tests/"]
reliability: high
---

## The skills that act (2026-11-08)

The skills that call writes declare them, and the reads their bodies name, on their `functions` line: `code-review` (the review, the verdict, the two comment functions on its pull request, the test run, the snapshot), `code-audit` (the snapshot, the conventions, `create_issue`, and the audit job's `start_repository_audit`, `stop_repository_audit` and, since 2026-11-10, `pause_repository_audit`, which its new section "An audit of a whole repository as a job", placed before pass 1 and so outside the batch text, asks for when the user wants a whole repository audited; its description carries "stop the audit", "pause the audit", "how is the audit going", "para a auditoria", "pausa a auditoria", "como está a auditoria"), `project-improvements` (the snapshot, `create_issue`), and two skills added that day: `src/Scout.Skills/skills/github-issues.md`, which opens an issue or comments on one from free text ("open an issue about the login bug", "abre uma issue sobre"), checks first that it is not tracked, and declares `create_issue` and `comment_on_issue`, of which it says since 2026-11-10, in the words the other three skills use, that the call queues the issue or the comment for the owner's approval, an Approve card waiting in the chat and in Approvals of Scout Desktop, and opens or posts nothing, never a URL and never a second call for the same one (`tests/Scout.Skills.Tests/BundledSkillsTests.cs:197`, `:214`, and `:224` that these four are the only skills naming such a function), and `src/Scout.Skills/skills/youtube-operator.md`, which declares all sixteen `YouTubeOperator` functions, reads the project state before every change, and leaves publishing and deleting to Scout Desktop. `project-vault` declares only the four reads its body names. `tests/Scout.Skills.Tests/BundledSkillsTests.cs:257`, `:269`, `:291`, `:306`, `:321` pin them; `tests/Scout.Host.Console.Tests/FunctionDeclarationTests.cs:95` that only these five declare writes, and `:106` that every function a body names is on its line.
