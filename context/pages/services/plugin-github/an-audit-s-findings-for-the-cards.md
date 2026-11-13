---
title: "Plugin.GitHub: An audit's findings for the cards (ADR 0040 v1.2, 2026-11-06)"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Plugin.GitHub service page."
page_type: service
status: active
description: "Read when a change touches: An audit's findings for the cards (ADR 0040 v1.2, 2026-11-06)."
source: ["src/Scout.Plugin.GitHub/", "tests/Scout.Plugin.GitHub.Tests/"]
reliability: high
---

## An audit's findings for the cards (ADR 0040 v1.2, 2026-11-06)

`present_audit_findings(repository, verdict, findings_json, commit, read, known_json)` (`src/Scout.Plugin.GitHub/GitHubPlugin.Findings.cs:99`) is the same hand-over for the `code-audit` skill, which has no pull request. `ReviewFindings.ParseAudit` (`src/Scout.Plugin.GitHub/ReviewFindings.cs:73`) applies the same rules without a number and adds `known_json`, the limitations the project already documents: blank or `[]` for none, otherwise a JSON array of at most 20 texts of 1 to 300 characters each, trimmed, refused with one sentence otherwise (`:145`). The block is the review's with `"kind":"audit"` first and no `number`, `known` written only when it has an item; a review's block carries no `kind` and stays as before. Each finding of either kind may carry `rule`, the project rule, ADR, convention or requirement it departs from, or "Defect" (`block` is High, `fix` Medium, `nit` Low on an audit). The 24,000-character limit, the refusal sentences and the answer "Findings recorded: N. End your answer with the verdict; the cards show the findings." are shared (`src/Scout.Plugin.GitHub/GitHubPlugin.Findings.cs:110`); both blocks open with the same fence line, so whichever of the two functions is called last in a turn leaves the only block. Nothing leaves the process: `tests/Scout.Plugin.GitHub.Tests/GitHubTokenFileHandlerTests.cs:142` invokes it with the rest and still sees only GETs. Opening the chosen issues stays `create_issue` above, each one queued for the owner's approval. Tests: `tests/Scout.Plugin.GitHub.Tests/GitHubPluginFindingsTests.cs` (an audit block with its kind, no number and the known list, `:219`; every invalid audit shape and the 24,000 limit, `:277`; rule and known kept and the blank ones left out, on an audit and on a review, `:292`; an audit after a review in the same turn leaves only the audit, `:322`).
