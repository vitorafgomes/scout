---
title: "{{TITLE}}"
version: "1.0"
date: {{DATE}}
updated: {{DATE}}
changes: "v1.0: the first settled entries"
page_type: established-patterns
status: active
description: "{{DESCRIPTION}}"
source:
  - chat
reliability: medium
---

# {{TITLE}}

> {{DESCRIPTION}}

Say who reads this page and when, for example "a reviewer checks here before reporting a finding",
and when an entry is added: in the change that settles the question. Name what does not belong here
and where it goes instead, such as an accepted limitation, which goes to
[[pages/roadmap/followups]].

---

## <Area of the code, such as Conversation and streaming>

**<What looks wrong, or what was chosen, as one bold sentence.>** Why it is right as it stands, where
it lives (`path:line`), and the test that locks it, by name. When an alternative was tried and
dropped, say what it was and why it lost.

**<Next entry.>** Same shape.

---

## Still open

Questions raised that are neither defects nor settled, kept here on purpose until the owner rules;
"None at the moment" when there are none.

---

## Links

[[pages/invariants/<rule>]] | [[pages/conventions/testing]]
