---
title: "{{TITLE}}"
version: "1.0"
date: {{DATE}}
updated: {{DATE}}
changes: "v1.0: the term and where it shows up in Scout"
page_type: knowledge
status: active
description: "{{DESCRIPTION}}"
source:
  - chat
reliability: medium
---

# {{TITLE}}

> {{DESCRIPTION}}

Scout's `knowledge` pages live under `pages/domain/` and describe one term or one component for
reference: what it means here, what it is made of, and what it is allowed to do.

## What it means in Scout

The meaning in this project, set apart from the everyday meaning and from any other part of Scout
that uses the same word differently.

## Where it lives

The projects and types that carry it, each with `path:line`, and the project page that owns it:
[[pages/services/<project>]].

## Its parts

The fields, states or functions it is made of, as a table when there are more than three, and a
drawing of its lifecycle when it moves through states.

## What it may write

Each call it makes that leaves the process, read or write, and how the write is declared, granted
and approved; "Nothing" when it only reads.

## Configuration

The section and the keys, with their defaults; secrets by name only.

## When it fails

What the caller gets on a timeout, an error status or a lost connection, and what the owner sees.

## Open questions

What is not settled, with whose decision it waits for.

## Links

- [[pages/invariants/<rule>]]
- [[pages/decisions/<NNNN-slug>]]
