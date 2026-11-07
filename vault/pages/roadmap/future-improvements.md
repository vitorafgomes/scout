---
title: "Future improvements: what widens Scout"
version: "1.0"
date: 2026-11-07
changes: "Initial initiative page"
page_type: roadmap
status: active
description: "The ordered list of improvements that widen Scout once the gap plan is done: project memory, ideas and roadmap from a repository, AI Sources with usage, Claude as a provider, Angular Material screens, audit validation, the code graph, credentials in a keyring, the YouTube operator."
source:
  - chat
reliability: medium
---

# Future improvements: what widens Scout

> When this plan is done, Scout remembers a project across sessions and restarts and sends the model only the minimum it needs, proposes ideas and a roadmap for a repository as cards that become issues, shows what each AI Source has left, answers through Claude, and audits with validated findings.

The sister page [[pages/roadmap/gap-plan]] holds what is broken today; nothing here starts before the gap it depends on is closed. Every item is built in this repository, by the team; no code is taken from another project, whatever its licence. Two outside projects lent ideas on 2026-11-07, recorded here in mechanism terms only: an autonomous coding desktop whose memory moved from a graph database to SQLite with full-text ranking, a context packer with budgets per type, a session observer that promotes signals to memories through a filter funnel with a trust gate against prompt injection, and a one-agent-per-dimension ideation with a fixed input and output contract; and a commercial security audit that builds a threat model first, runs independent scans that stop when nothing new appears, keeps partial results, and gives each finding a validation status and a dismissal reason.

## Why now

The model never knows the owner's projects. Whatever matters has to be kept on Scout's side, classified, with a defined minimum to send for each kind of continuation. The gap plan's G4 lays the first tables; this plan makes them the project memory and builds the features that read it.

## Scope

- **In scope:** the items in the table, each one version, each with a short ADR before code.
- **Out of scope:** anything on the gap plan; the Teams adapter and the live voice work, which keep their own pages.

## Target outcome

By 2027-02-28: Scout gives a verdict on a pull request, approve or request changes, with the project's tests run in isolation when the repository's CI does not run them; a question about a project the owner worked on last week is answered with that week's facts without a re-read; a repository's ideas appear as cards and a chosen idea opens an issue; the AI Sources screen shows credits or plan window per source; Claude answers through the same abstraction as the other providers.

## Status

- **As of 2026-11-07:** nothing started; F3's prototype is approved and its plumbing mapped; the YouTube operator's presenter lanes (F9) are interrupted and wait for the gap plan.
- **Next milestone:** the ADR for F1, written on top of G4's tables.
- **Blocker:** G3 and G4 on the gap plan.

## Items

| # | Item | Depends on |
|---|---|---|
| F1 | Project memory in SQLite: three layers (conversation per channel on disk with a summary of what each turn did and the files it read; working memory per task, which is G4's progress; long-term facts and decisions), `remember` and `recall` as functions for the model, an extractor after each turn that writes file purposes, patterns, gotchas and recommendations as structured records, a promotion funnel so a signal becomes a memory only after it holds, a trust gate that marks anything derived from a function result or a web page as data and never as a rule, injection between steps when a gotcha applies, a context pack with a fixed budget per part (profile, file map, patterns and gotchas, the last three session records, open items) of about 12,000 characters in all, an export to Markdown for the owner to read | G4 |
| F2 | Ideas and roadmap from a repository: the audit's mechanism with dimensions of opportunity instead of defect (what the code reveals: pattern extensions, architecture openings, configuration, utilities, UI, data handling, infrastructure), one turn per dimension with a fixed input (the snapshot's structure, what already exists and is already planned, the memory's file map and patterns) and a fixed output (id, title, description, rationale, affected files, effort, approach, what it builds upon), deduplicated against open findings and issues, shown as cards, a chosen idea opens an issue | G4, F1 |
| F3 | AI Sources cards with credits, plan window and Scout's usage per source, in three phases (the approved prototype; no existing function lost) | none |
| F4 | Claude as a provider through the API, and a spike on the owner's Claude subscription | F3 |
| F5 | The screens migrated to Angular Material one per version ([[pages/decisions/0041-angular-material-in-the-renderer]]) | none |
| F6 | The audit with validated findings: a threat model of the repository first, dimensions run as independent jobs that stop when nothing new appears, a validation status per finding, always from reading the source since Scout never runs the project's code, root cause and path, findings persisted across audits, a dismissal with its reason | G4 |
| F7 | A code graph over the snapshot for "what breaks if I change X": Roslyn for C#, a parser per other language, a closure table in SQLite | G3 |
| F8 | Feature credentials in the system keyring instead of memory ([[pages/roadmap/followups]], entry of 2026-10-23) | none |
| F9 | The YouTube channel operator's presenter: generated inside the scene with character consistency, then a dialogue with two presenters ([[pages/roadmap/youtube-channel-operator]]) | none |
| F10 | The verdict on a pull request: approve or request changes as a declared, per-action approved write beside the commenting review ([[pages/decisions/0040-review-findings-as-cards-in-chat]], [[pages/decisions/0032-per-feature-write-permissions]]); the project's tests as part of the verdict, first by reading the pull request's own check runs (a read), then, for a repository without a CI that runs them or when the review asks for one more check, by running the project's own test command inside a disposable container on the owner's machine: the stack detected from the manifests, the command from the CI workflow, the manifest or the conventions profile, the snapshot copied in, no secret of the owner's inside, no network after the dependencies are restored, an unprivileged user, a read-only filesystem but the work folder, CPU, memory and time caps, the container discarded at the end; without a container runtime the verdict says the tests did not run and why; the review card shows the tests line and the two verdict buttons. The owner's words (2026-11-07): "o Scout tem que poder aprovar um PR ou rejeitar, esse é o objetivo, validar tudo", "o usuário ter um docker onde o Scout execute tudo isolado" | G4, F1 |

Order: F1, F10, F3, F2, F5 interleaved one screen at a time, then F6 and F7 once the audit is in daily use; F8 and F9 when the owner asks. F10 is the owner's stated goal ("validar tudo") and comes right after the memory it reads from.

## Decisions pending

- An ADR per item, written before code: F1 (tables, functions, budgets, the trust gate), F10 (the verdict as a write, the container's rules, the test command's sources), F2 (dimensions, contracts), F4 (the provider), F6 (validation status), F7 (the graph).

## Services affected

- [[pages/services/knowledge]] (F1, F2, F6, F7)
- [[pages/services/skills]] (F2, F6)
- [[pages/services/conversation]] (F1)
- [[pages/services/llm]] (F4)
- [[pages/services/desktop-ui]] (F3, F5)
- [[pages/services/plugin-github]] (F2, F6)

## Risks

- **Risk:** the memory stores what the model says rather than what held. **Mitigation:** the promotion funnel and the trust gate; a memory names its source turn and is deletable from the desktop.
- **Risk:** the context pack grows until every turn is expensive. **Mitigation:** a fixed budget per part, measured in the harness on a real repository before each version.
- **Risk:** a pull request's code is hostile and the test run is the attack. **Mitigation:** the container's rules in F10 are the security boundary, decided in its ADR before code; without them the tests do not run.
- **Risk:** F2 proposes ideas the owner never asked for. **Mitigation:** ideas are cards, nothing happens until one is chosen, and an issue is a declared write under [[pages/decisions/0032-per-feature-write-permissions]].

## Related

- Sister page: [[pages/roadmap/gap-plan]]
- The diagnosis and the survey behind both plans: [[pages/roadmap/project-memory-spike]]
- Parent initiatives: [[pages/roadmap/code-quality-assistant]], [[pages/roadmap/chatgpt-plan-provider]], [[pages/roadmap/youtube-channel-operator]]
