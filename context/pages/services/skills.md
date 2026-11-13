---
title: "Skills"
version: "1.17"
date: 2026-10-21
changes: "v1.17 (2026-11-11) a skill index build or a query embedding the AI Source answers HTTP 404 for, the embedding model not installed, is logged once as a Warning naming the model and where to download it, without a stack; the generic failure line says skills are picked by the words of the message."
updated: 2026-11-11
page_type: service
status: active
description: "Loads markdown skill files, each with the functions its turn may call, and offers them to the model: embedding-matched injection into the system prompt, a pick by the words of the message and a skill catalog in the prompt when the provider has no embeddings, and list_skills and load_skill tools."
source: ["src/Scout.Skills/", "tests/Scout.Skills.Tests/"]
reliability: high
---

# Skills

> Process guides stored as markdown, picked by similarity or by the model on demand.

## Responsibility

Owns skill file parsing, the catalog, the selector and prompt composition. Does not own embeddings ([[pages/services/llm]]) or the router that calls the selector ([[pages/services/conversation]]).

## Rules

The rules this project must keep, and what to read before changing it, are in `.claude/rules/skills.md`. Claude Code loads that file when a task touches the project.

## Topics

Read the page your change touches, not all of them.

| Page | Read when a change touches | Characters |
|---|---|---|
| [[pages/services/skills/public-contract\|Public contract]] | Public contract | 1476 |
| [[pages/services/skills/how-it-works\|How it works]] | How it works | 5541 |
| [[pages/services/skills/the-pick-by-words\|The pick by words (2026-11-08)]] | The pick by words (2026-11-08) | 2181 |
| [[pages/services/skills/a-review-or-an-audit\|A review or an audit (2026-11-10)]] | A review or an audit (2026-11-10) | 2909 |
| [[pages/services/skills/a-review-writes-on-its-pull-request-an-audit-ope\|A review writes on its pull request, an audit opens issues (2026-11-10)]] | A review writes on its pull request, an audit opens issues (2026-11-10) | 2383 |
| [[pages/services/skills/the-review-walks-the-owner-s-ten-steps\|The review walks the owner's ten steps (2026-11-10)]] | The review walks the owner's ten steps (2026-11-10) | 2524 |
| [[pages/services/skills/four-review-criteria\|Four review criteria (2026-11-11)]] | Four review criteria (2026-11-11) | 3645 |
| [[pages/services/skills/the-skills-that-act\|The skills that act (2026-11-08)]] | The skills that act (2026-11-08) | 2385 |
| [[pages/services/skills/the-code-quality-skills\|The code-quality skills (2026-11-06)]] | The code-quality skills (2026-11-06) | 10456 |
| [[pages/services/skills/hot-spots\|Hot spots]] | Hot spots | 1316 |
| [[pages/services/skills/testing-footprint\|Testing footprint]] | Testing footprint | 1275 |

## Related

Direct project references: none. Referenced by [[pages/services/conversation]]. Map: [[pages/architecture/project-map]]. Dated changes: [[pages/history/skills]].
