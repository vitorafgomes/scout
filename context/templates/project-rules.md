---
paths:
  - "src/<Project>/**"
  - "tests/<Project>.Tests/**"
---

# <Project>

REQUIRED. Two or three sentences: what the project owns and what it does not.

This file is saved as `.claude/rules/<project>.md`, not in the context folder. It is loaded
whenever a task touches the paths above, so every line is paid for often: keep it under 6,000
characters. A rule is one sentence that says what holds. The proof goes on a topic page.

## Rules this project must keep

REQUIRED. One line per rule: the invariant or decision it comes from, in bold, then how this
project keeps it.

- **<Title of the invariant or decision>.** Here: <what holds in this project, in one sentence>.

The full statement of each, with the code and tests that hold it:
`context/pages/services/<project>/how-the-rules-are-kept.md`.

## Pages that govern this project

Read one when your change touches what its title names.

- [[pages/invariants/<rule>|<Title>]]
- [[pages/decisions/<NNNN-slug>|<Title>]] - <which part of the project it governs>

## What to read for a change

REQUIRED. One row per topic page. The left column names something a reader can see in the
change: a type, a folder, a configuration key, a screen.

| If the change touches | Read |
|---|---|
| <what in the code> | `context/pages/services/<project>/<topic>.md` |
