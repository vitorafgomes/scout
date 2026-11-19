---
title: "Audit: Open items"
version: "1.3"
date: 2026-11-13
updated: 2026-11-19
changes: "v1.3 (2026-11-19): the review job's open items after the waves and the bell. v1.2 (2026-11-18): the review job's open items"
page_type: service
status: active
description: "Read when a change touches: Open items."
source: ["src/Scout.Audit/", "tests/Scout.Audit.Tests/", "tests/Scout.ArchTests/PersistenceTests.cs", "context/pages/decisions/0052-persistence-behind-stores-on-entity-framework.md", "src/Scout.Host.Console/HostComposition.cs", "src/Scout.Host.Console/ReviewEnvironmentAdapter.cs", "tests/Scout.Host.Console.Tests/HostAuditTurnTests.cs", "context/pages/decisions/0042-audit-as-a-job-with-saved-progress.md"]
reliability: high
---

## Open items

- The pause notice ends "when the AI Source has capacity again" for every reason; for a rejected key the owner has to fix the key in Scout Desktop first, and for a missing model install it or wait for its download, which the sentence does not say. The desktop's bell shows its own sentence per reason since 2026-11-12, read from the reason's text ([[pages/services/desktop-ui]], Notification center); the chat still gets the worker's sentence.
- An area with no new findings posts one line only to a job the desktop's local chat holds, since 2026-11-14 (the owner's decision); in Discord, Slack and Teams it still posts nothing, so the progress there shows only when an area with findings ends. An area not covered posts nothing anywhere, so the desktop's bar stands still on it until the next area ends.
- Discord and Slack cut a message well under 30,000 characters; the notifier does not expose the origin's limit, so a block posted there can still arrive cut.
- The final verdict is computed from the severities and the coverage; the model's per-batch verdicts ride only on the batch posts.
- A file longer than a read returns, read in its first 20,000 characters, is named as partly read but does not make the verdict `incomplete`; only an area with a file never read, or one not covered, does.
- A partly read area is not run again for its unread files; the owner starts a new audit to cover them.
- The earlier titles a batch is told can no longer come from a batch turn, because a finding outside a batch's files is dropped and a file belongs to one batch; the request still lists them for rows kept before that rule.
- The snapshot is found by the short commit the snapshot function's answer names, in parentheses; a change to that sentence's shape in the GitHub plugin would turn every start into a refusal returned as it is.
- The owner pauses, continues and stops a review from the bell of Scout Desktop since 2026-11-19, and the notices are posted; no chat function pauses or stops a review yet, and a start resumes a paused one ([[pages/roadmap/followups]]).
- A review stopped while paused gets its card through a hand-off kept in memory: a host restart before the worker's next wake loses that card ([[pages/roadmap/followups]]).
- A wave begun just before the 60 minutes runs to its end, so a review can run past them by up to a wave; the time of a batch a provider limit interrupted is not counted.
- The audit library reads `Scout:Llm:Provider` to choose the default of batches at once, a second place that knows which provider is the local default.
- A review batch is capped by the configured characters only; a cap from the model's context window is not built, and a diff larger than one batch is named only on the card, never at the start.
- The desktop's card shows the measured line and the notes since 2026-11-19, but still offers Approve on a reading that is not complete ([[pages/roadmap/followups]]).
- A review that throws on every wake is picked before every audit, so it holds them all until it is stopped ([[pages/roadmap/followups]]).
