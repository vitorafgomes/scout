---
title: "{{PROJECT}}: {{TOPIC}}"
version: "1.0"
date: {{DATE}}
updated: {{DATE}}
changes: "v1.0: first version"
page_type: service
status: active
description: "Read when a change touches: {{WHAT_IN_THE_CODE}}."
source:
  - src/<Project>/<folder or file>
reliability: high
---

One topic of one project: at most 12,000 characters. When it grows past that, split it and add
the new page to the map, [[pages/services/<project>]], and to the table in
`.claude/rules/<project>.md`.

## Contract

REQUIRED when the topic has a public surface. What other code may rely on: the types, the
members, the configuration keys. Each statement names the `file:line` that holds it.

## How it works

REQUIRED. What happens, in the order it happens. Each claim a reader could doubt names the
`file:line` or the test that shows it.

## Sharp edges

Lines where a small change has a wide effect, each with its `file:line` and what breaks.

## Tests

The tests that cover this topic, and the way they reach an external boundary when there is one.
