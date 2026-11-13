---
title: "{{TITLE}}"
version: "1.0"
date: {{DATE}}
updated: {{DATE}}
changes: "v1.0: the decision as first written"
page_type: adr
status: proposed
description: "{{DESCRIPTION}}"
supersedes: ""
superseded_by: ""
source:
  - chat
reliability: high
---

# {{TITLE}}

> {{DESCRIPTION}}

The title reads `ADR NNNN - <the decision as a plain sentence>`, with the next free number, and the
file is `pages/decisions/NNNN-<slug>.md`. The quote above is the decision itself, short enough to
repeat from memory.

## Status

Proposed on {{DATE}}. When the owner rules, the state above moves to accepted and this line says
when and with which conditions. A later ADR that replaces this one is named here and in
`superseded_by:`.

## The problem

What forced a choice: the request in the owner's words, the constraint in the code, the failure seen.
Only what someone who was not there needs in order to judge the decision.

## The decision

What Scout does from now on, stated as rules a reviewer can check: the types, the limits, the
configuration keys, the screens, the order of the steps.

## Calls that leave the process

One line per call the decision adds or changes: where it goes, and whether it reads, writes or writes
irreversibly. A write names the feature that declares it, the grant the owner gives in the desktop
and, when it cannot be undone, the approval per action. Write "None" when nothing leaves the
process.

## What follows

- **Gained:** what gets better.
- **Paid:** what gets worse or harder.
- **To watch:** what is neither, but will surprise someone later.

## Rejected options

- **<option>:** why it lost. Doing nothing counts as an option.

## How it is held

The tests that fail if the decision is broken, named and cited as `path:line`, and the invariant,
convention or check that carries it. A decision nothing holds is a wish.

## Links

- Projects it shapes: [[pages/services/<project>]]
- Invariants it creates or leans on: [[pages/invariants/<rule>]]
- Decisions it amends or replaces: [[pages/decisions/<NNNN-slug>]]

## Amendments

Each later change to this decision goes here as `### Amendment YYYY-MM-DD: <what changed>`, newest
last, with its own reasons and tests. The text above is not rewritten.
