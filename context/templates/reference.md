---
title: "{{TITLE}}"
version: "1.0"
date: {{DATE}}
changes: "v1.0: the first dated change"
page_type: reference
status: artefact
description: "{{DESCRIPTION}}"
source:
  - chat
reliability: high
---

# {{TITLE}}

> Dated changes to [[pages/services/<project>]]; that page says how the project works now.

In Scout a `reference` page is a project's history: the title is `<Project> - history`, the file is
`pages/history/<project>.md`, and the description reads "What changed in <Project> and when." Each
change gets one section, the newest at the top, added in the branch that made the change.

## YYYY-MM-DD - <the change, as one plain sentence>

- **Asked for:** the request or the finding, in the words it arrived in.
- **What changed:** the behaviour after the change, with the types and lines involved.
- **Not changed:** what someone might expect to have moved and did not, and why.
- **Tests:** the tests that lock it, by name and file.
