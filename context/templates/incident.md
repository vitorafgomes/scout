---
title: "{{TITLE}}"
version: "1.0"
date: {{DATE}}
updated: {{DATE}}
changes: "v1.0: what happened, its cause and its fix"
page_type: incident
status: active
description: "{{DESCRIPTION}}"
source:
  - chat
reliability: high
---

# {{TITLE}}

> {{DESCRIPTION}}

An incident page is written when a running Scout (the worker, a host or the desktop app) was
affected; a fault found only in the code goes on the project's history page instead. The quote says
what the owner experienced and for how long.

## What the owner saw

The symptom in the owner's words: the message in the chat, the state of a card, the line in the log.

## Sequence

Times in UTC, one line per event: first sign, when it was noticed, what was tried, when it
stopped.

## Cause

The condition that made the failure possible, shown with evidence (log lines, a trace, a test that
reproduces it), apart from what merely triggered it.

## Fix

The change that ended it and the regression test that fails without it, by name and file.

## Rules involved

The invariant that was broken or that this incident adds, linked: [[pages/invariants/<rule>]].

## Follow-ups

Each remaining item as an entry on [[pages/roadmap/followups]], linked from here.

## Links

- [[pages/services/<project>]]
- [[pages/history/<project>]]
