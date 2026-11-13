---
paths:
  - "src/Scout.Skills/**"
  - "tests/Scout.Skills.Tests/**"
---

# Skills

Owns skill file parsing, the catalog, the selector and prompt composition. Does not own embeddings ([[pages/services/llm]]) or the router that calls the selector ([[pages/services/conversation]]).

## Rules this project must keep

- [[pages/invariants/no-executive-writes]]: both tools read; nothing writes a file. A skill's `functions` line is what lets its turn reach a write at all ([[pages/decisions/0054-functions-scoped-per-skill-and-job]]).
- [[pages/invariants/pluggable-boundaries]]: the plugin joins the kernel through DI, not by name.
- [[pages/invariants/review-writes-on-its-pull-request]]: a review's functions line holds writes on its pull request only, an audit's no write on a pull request.

## Pages that govern this project

Read one when your change touches what its title names.

- [[pages/invariants/no-executive-writes|Scout performs descriptive writes only]]
- [[pages/invariants/pluggable-boundaries|Adapters, LLM providers and plugins are swapped by configuration]]
- [[pages/services/conversation|Conversation]]
- [[pages/conventions/testing|Testing]]

## What to read for a change

Find the row your change matches and read that page. Read one or two rows, not the table.

| If the change touches | Read |
|---|---|
| Public contract | `context/pages/services/skills/public-contract.md` |
| How it works | `context/pages/services/skills/how-it-works.md` |
| The pick by words (2026-11-08) | `context/pages/services/skills/the-pick-by-words.md` |
| A review or an audit (2026-11-10) | `context/pages/services/skills/a-review-or-an-audit.md` |
| A review writes on its pull request, an audit opens issues (2026-11-10) | `context/pages/services/skills/a-review-writes-on-its-pull-request-an-audit-ope.md` |
| The review walks the owner's ten steps (2026-11-10) | `context/pages/services/skills/the-review-walks-the-owner-s-ten-steps.md` |
| Four review criteria (2026-11-11) | `context/pages/services/skills/four-review-criteria.md` |
| The skills that act (2026-11-08) | `context/pages/services/skills/the-skills-that-act.md` |
| The code-quality skills (2026-11-06) | `context/pages/services/skills/the-code-quality-skills.md` |
| Hot spots | `context/pages/services/skills/hot-spots.md` |
| Testing footprint | `context/pages/services/skills/testing-footprint.md` |

For where a type lives or who calls it, ask the code graph (`graphify explain "<Type>"`, `graphify affected "<Type>"`).
