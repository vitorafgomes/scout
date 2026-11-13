---
title: "{{TITLE}}"
version: "1.0"
date: {{DATE}}
updated: {{DATE}}
changes: "v1.0: the rule, where it is held and the tests that prove it"
page_type: invariant
status: active
description: "{{DESCRIPTION}}"
enforced_by:
  - "src/<Project>/<File>.cs:<line>"
verified_by:
  - "tests/<Project>.Tests/<File>Tests.cs:<line>"
source:
  - chat
reliability: high
---

# {{TITLE}}

> {{DESCRIPTION}}

The title states the rule itself, for example "Secrets never live in source". The quote says it so
that a single counter-example would prove it broken.

## The rule

1. The rule in one numbered sentence per obligation, in the words the owner or the ADR used.

## Why Scout needs it

The failure it prevents: the incident, the finding, the owner's request or the decision behind it,
linked.

## Reach

- **Covers:** the projects, paths, calls or screens bound by it.
- **Leaves out:** the nearby cases it does not govern, said outright.

## Held by

One bullet per entry of `enforced_by:`, the same `path:line`, saying what that code does to keep the
rule.

## Proved by

One bullet per entry of `verified_by:`, the same `path:line`, naming the test and the input that
would break the rule.

## Gaps and exceptions

Places where the rule is knowingly not applied, or not yet held by code, each with its reason and
the tracker entry that follows it up.

## Changing the rule

Nothing breaks this rule quietly. A change that needs to starts with an ADR that replaces or narrows
it; this page then turns `deprecated` and links that ADR, and the code that held the rule changes in
the same branch.

## Links

- Projects bound by it: [[pages/services/<project>]]
- The decision behind it: [[pages/decisions/<NNNN-slug>]]
- Related rules: [[pages/invariants/<rule>]]
