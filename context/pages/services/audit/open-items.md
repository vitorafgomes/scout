---
title: "Audit: Open items"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Audit service page."
page_type: service
status: active
description: "Read when a change touches: Open items."
source: ["src/Scout.Audit/", "tests/Scout.Audit.Tests/", "tests/Scout.ArchTests/PersistenceTests.cs", "context/pages/decisions/0052-persistence-behind-stores-on-entity-framework.md", "src/Scout.Host.Console/HostComposition.cs", "src/Scout.Host.Console/ReviewEnvironmentAdapter.cs", "tests/Scout.Host.Console.Tests/HostAuditTurnTests.cs", "context/pages/decisions/0042-audit-as-a-job-with-saved-progress.md"]
reliability: high
---

## Open items

- The pause notice ends "when the AI Source has capacity again" for every reason; for a rejected key the owner has to fix the key in Scout Desktop first, and for a missing model install it or wait for its download, which the sentence does not say. The desktop's bell shows its own sentence per reason since 2026-11-12, read from the reason's text ([[pages/services/desktop-ui]], Notification center); the chat still gets the worker's sentence.
- An area with no new findings posts nothing, so the desktop's bell learns of the audit only when an area with findings ends, and its progress stays where it was until then; the bell's running entry has no findings count to show either. Both are owed to the bell ([[pages/roadmap/prototype-migration]]).
- Discord and Slack cut a message well under 30,000 characters; the notifier does not expose the origin's limit, so a block posted there can still arrive cut.
- The final verdict is computed from the severities and the coverage; the model's per-batch verdicts ride only on the batch posts.
- A file longer than a read returns, read in its first 20,000 characters, is named as partly read but does not make the verdict `incomplete`; only an area with a file never read, or one not covered, does.
- A partly read area is not run again for its unread files; the owner starts a new audit to cover them.
- The earlier titles a batch is told can no longer come from a batch turn, because a finding outside a batch's files is dropped and a file belongs to one batch; the request still lists them for rows kept before that rule.
- The snapshot is found by the short commit the snapshot function's answer names, in parentheses; a change to that sentence's shape in the GitHub plugin would turn every start into a refusal returned as it is.
