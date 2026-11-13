---
title: "{{TITLE}}"
version: "1.0"
date: {{DATE}}
updated: {{DATE}}
changes: "v1.0: the procedure as first run"
page_type: runbook
status: active
description: "{{DESCRIPTION}}"
source:
  - chat
reliability: high
---

# {{TITLE}}

> {{DESCRIPTION}}

The quote says when to reach for this page, in the form "Run this when ...".

## Run it when

The concrete signals: the owner's request, the symptom on a screen, the line in the worker's log.

## Check first

- [ ] Each condition that must hold before the first step, with the command that shows it.

## Access it needs

The permissions, the configuration values and the secrets involved, by name only: a value never
appears on this page.

## Procedure

1. **<what this step does>**
   ```bash
   <command>
   ```
   Expected: the output or the state that means the step worked.

A step that cannot be undone says so in bold and names what has to be true before running it.

## Confirm it worked

The positive signal that the whole procedure succeeded, not only the absence of an error.

## Known failures

### <the message or the symptom, as it appears>

What it means, and the numbered steps that clear it.

## Undo

How to return to the state before the procedure, step by step, and from which step that is no
longer possible.

## Links

- [[pages/services/<project>]]
- [[pages/decisions/<NNNN-slug>]]
