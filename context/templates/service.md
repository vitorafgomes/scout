---
title: "{{TITLE}}"
version: "1.0"
date: {{DATE}}
updated: {{DATE}}
changes: "v1.0: the project as it stands on {{DATE}}"
page_type: service
status: active
description: "{{DESCRIPTION}}"
source:
  - src/<Project>/
  - tests/<Project>.Tests/
reliability: high
---

# {{TITLE}}

The map of one project. It says what the project is for and lists its topic pages. How a topic
works goes on the topic page; the rules go in `.claude/rules/<project>.md`; dated changes go on
[[pages/history/<project>]]. `changes` holds the newest note only.

## Responsibility

REQUIRED. Two or three sentences: what this project owns, and what it does not own, naming the
project that does.

## Rules

The rules this project must keep, and what to read before changing it, are in
`.claude/rules/<project>.md`. Claude Code loads that file when a task touches the project.

## Topics

REQUIRED. One row per topic page. The middle column completes the sentence "read this page when
a change touches ...".

| Page | Read when a change touches | Characters |
|---|---|---|
| [[pages/services/<project>/<topic>\|<Topic>]] | <what in the code this page is about> | <size> |

## Related

REQUIRED. Projects this one references, projects that reference it, and its history page.
