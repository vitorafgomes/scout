---
title: "Gap plan: what blocks real use today"
version: "1.0"
date: 2026-11-07
changes: "Initial initiative page"
page_type: roadmap
status: active
description: "The ordered list of defects and missing pieces that stop the owner from using Scout on a real repository today; one change per version, the sister page of the future-improvements plan."
source:
  - chat
  - src/Scout.Conversation/Memory/ConversationMemoryOptions.cs
  - src/Scout.Knowledge/ProjectKnowledgeService.cs
  - src/Scout.Skills/skills/code-audit.md
reliability: medium
---

# Gap plan: what blocks real use today

> When this plan is done, the owner audits a real repository from the desktop, the audit covers the whole repository across several turns and survives a restart, the floating chat behaves under every combination of state and action, and nothing in the chat depends on the AI provider having embeddings.

This page lists only what is broken or missing today. What widens Scout lives on [[pages/roadmap/future-improvements]]. Every item here is built in this repository, by the team; no code is taken from another project, whatever its licence; another project may only lend an idea, and the page that uses the idea says so in mechanism terms, never by the project's name.

## Why now

On 2026-11-07 the owner audited a real repository from the floating chat and got two findings before the audit stopped; asking Scout to continue started from nothing. The reasons are in the design, not in the model:

- The audit is one answer. The `code-audit` skill audits six dimensions in one turn and, on a large repository, tells the model to stop and ask the user for an area; every provider's function loop caps the calls in a turn (40 by default on the Microsoft.Extensions.AI path, `src/Scout.Llm/Providers/ChatGptPlan/ChatGptPlanLlmProviderFactory.cs:106`; Semantic Kernel's own cap on the other paths, [[pages/services/llm]]) and the model stops well before that.
- Since G3 (2026-11-07) a provider with no embedding generator (the ChatGPT plan today; any provider the owner connects tomorrow) still has project search: `search_project` and `find_symbol` answer through the full-text index ([[pages/services/knowledge]], `src/Scout.Knowledge/ProjectKnowledgeService.cs:62`).
- The conversation memory keeps only text: the last six turns, 8,000 characters replayed, in the worker's RAM, expiring after 30 idle minutes ([[pages/services/conversation]], `src/Scout.Conversation/Memory/ConversationMemoryOptions.cs:22`). The files read and the findings are not in it, and the audit's reply is short by design because the cards carry the details ([[pages/decisions/0040-review-findings-as-cards-in-chat]]).

Two process rules apply from this date, independent of any item below:

- A change to the chat, a review or an audit runs through the worker harness against a real repository before an AppImage is built; the report carries the number of function calls, the files read and the findings.
- A change to a stateful screen (cards, the chat composer, dialogs) ships with a table-driven spec over state, action and variant, and the expected outcomes are published for the owner to confirm before the AppImage.

## Scope

- **In scope:** the items in the table, each one version, in the order given unless a dependency says otherwise.
- **Out of scope:** long-term memory, ideas and roadmap generation, new AI Sources, Angular Material screen migrations, the code graph, the YouTube operator; they are on [[pages/roadmap/future-improvements]].

## Target outcome

By 2026-12-15 the owner runs an audit of a repository with more than 500 files from the floating chat, the audit reaches every area without a prompt from the owner, the findings accumulate on the cards across turns and across a restart of Scout, and the floating chat's matrix spec is green.

## Status

- **As of 2026-11-07:** the owner's decision: the whole plan ships as one pull request from `feat/gap-plan`, opened only when everything is done, one commit per step and per review fix ("um PR com tudo isso"). G1 merged (PR #113); G2 merged (PR #108, PR #109); G3 committed on the bundle; G8 lanes A to D committed at their first checkpoint; G4, G5 and the rest of G8 in progress; G7 waits for the owner's word.
- **Next milestone:** the verdict's executor (the card click as the owner's approval), the G3 performance fixes, G5's shared picks, then G4; then one integration AppImage, the expected-behaviour table, and the single pull request.
- **Blocker:** none.

## Items

| # | Item | State | Version |
|---|---|---|---|
| G1 | Floating chat (`feat/quick-ask-redesign`, 2026-11-07): the approved layout (mic inside the field, Send becomes Stop, tools collapsed, one scroll), no send while a reply runs, Space and Escape only in the question box, a partial reply kept on failure, the text and screenshot kept on a refusal, scroll only when at the bottom, No collapses the card and hides the footer when nothing is left, matrix specs | in progress | 1 |
| G2 | An empty reply logged with its reason and the ChatGPT plan's usage limit said in the chat (PR #108); the skill catalog in the prompt when the provider has no embeddings (PR #109) | PRs open | merge |
| G3 | Full-text search (SQLite FTS5) over the existing chunk index, so `search_project` and `find_symbol` answer on every provider, with or without embeddings; the semantic search stays on top when embeddings exist | committed on `feat/gap-plan` (2026-11-07) | 2 |
| G4 | After G8. The audit as a job with saved progress: Scout builds the coverage plan from the snapshot's folder structure ([[pages/decisions/0039-repository-snapshot-under-the-data-folder]]), each batch is one turn with a closed scope, after each batch Scout records the files read, the findings and what is left in SQLite ([[pages/decisions/0037-meetings-kept-locally-in-sqlite]] is the precedent), the next batch starts on its own, a partial result is kept, findings are merged by path and title across batches and the cards accumulate them; needs an ADR | not started | 4 |
| G5 | Picks on the findings cards shared between the main window and the floating chat, so a review posted in one does not ask for approval in the other | not started | 5 |
| G6 | Clear on the Chat screen also resets the worker's memory for the local channel, and the transcript sent with a question stays out of the stored turn ([[pages/roadmap/followups]], entries of 2026-10-30 and 2026-11-04) | not started | 6 |
| G7 | The Production screen with the project detail below and the operator podcast lane, both built and waiting for the owner's word to open the PRs | ready | merge |
| G8 | **The next deliverable after G3**, with comments on a pull request's conversation, on an existing issue and replies to a review comment as three declared writes ("ele tem que permitir") (the owner, 2026-11-07: "nosso próximo entregável tem que fazer tudo isso"): the verdict on a pull request, approve or request changes, as a declared write approved per action, with the project's tests as part of it: the pull request's check runs read first; then, for a repository whose CI does not run them or when the review asks for one more check, the project's own test command run inside a disposable, isolated container on the owner's machine; the review card shows the tests line and the two verdict buttons. Design and the container's rules: [[pages/decisions/0043-the-verdict-on-a-pull-request]]. In the same deliverable, optional (the owner: "isso tem que ser opcional"): Scout signs its reviews, verdicts and issues as its own GitHub App's bot instead of the owner, through an App the owner creates from the desktop with GitHub's manifest flow ([[pages/decisions/0044-scout-acts-on-github-as-its-own-app]]); without it, today's sign-in acting as the owner stays the default | ADRs drafted | 3 |

## Decisions pending

- [[pages/decisions/0044-scout-acts-on-github-as-its-own-app]] for G8's optional bot identity, *status: proposed* on 2026-11-07.
- [[pages/decisions/0043-the-verdict-on-a-pull-request]] for G8, *status: proposed* on 2026-11-07; the container's rules are the security boundary and are decided there before code.
- [[pages/decisions/0042-audit-as-a-job-with-saved-progress]] for G4 (the audit job, its tables and the batch flow), *status: proposed* on 2026-11-07; it also fixes the minimum memory the future plan builds on.

## Services affected

- [[pages/services/desktop-quick-ask]] (G1, G5, G6)
- [[pages/services/knowledge]] (G3, G4)
- [[pages/services/skills]] (G2, G4)
- [[pages/services/conversation]] (G2, G4, G6)
- [[pages/services/plugin-github]] (G4)
- [[pages/services/llm]] (G2)

## Risks

- **Risk:** G4 grows into the long-term memory and never ships. **Mitigation:** G4 stores only work items, the files read per batch and the findings; patterns, gotchas and recall are F1 on the future plan.
- **Risk:** G3 returns noise on a large repository. **Mitigation:** BM25 ranking from FTS5, the top results capped, and the audit's own `search_code` kept as the second path.

## Related

- Parent initiative for G4: [[pages/roadmap/code-quality-assistant]] (phases 4 and 5)
- Sister page: [[pages/roadmap/future-improvements]]
- The diagnosis and the survey behind both plans: [[pages/roadmap/project-memory-spike]]
- ADRs: [[pages/decisions/0040-review-findings-as-cards-in-chat]], [[pages/decisions/0039-repository-snapshot-under-the-data-folder]]
