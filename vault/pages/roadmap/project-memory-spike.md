---
title: "Project memory spike"
version: "1.0"
date: 2026-11-07
changes: "Initial research page"
page_type: roadmap
status: active
description: "Research: why a whole-repository audit stops early and forgets, what two outside projects do about memory, audits, ideas and roadmaps (mechanisms only), and the design both plans adopt."
source:
  - chat
  - src/Scout.Conversation/Memory/ConversationMemoryOptions.cs
  - src/Scout.Knowledge/ProjectKnowledgeService.cs
  - src/Scout.Skills/skills/code-audit.md
reliability: medium
---

# Project memory spike

> Research of 2026-11-07 behind [[pages/roadmap/gap-plan]] and [[pages/roadmap/future-improvements]]: the diagnosis, the survey and the design. Ideas only were taken from outside; every line of code is written here.

## The problem as the owner met it

From the floating chat the owner asked for an audit of a real repository with many files. Scout returned two findings and stopped; asked to continue, it had forgotten what it had read. The owner's framing: the model will never know the project, so what matters has to be kept on Scout's side, classified, with a defined minimum to send for each kind of continuation.

## Diagnosis

- **One answer per audit.** `src/Scout.Skills/skills/code-audit.md` audits six dimensions in one turn and tells the model, on a large repository, to stop and ask for an area. Every provider's function loop caps the calls in a turn (40 by default on the Microsoft.Extensions.AI path, Semantic Kernel's own cap on the others, [[pages/services/llm]]) and the model stops well before.
- **No search without embeddings.** `search_project` and `find_symbol` answer that project search is not available when the provider has no embedding generator ([[pages/services/knowledge]]), which is the case on the ChatGPT plan today and may be the case on any provider the owner connects; whole-file reads exhaust the context.
- **Text-only memory.** The conversation memory stores the question and the reply text, in RAM, 6 turns, 8,000 characters, 30 idle minutes ([[pages/services/conversation]]); the files read, the search results and the findings are not in it, and the audit's reply is short by design because the cards carry the details ([[pages/decisions/0040-review-findings-as-cards-in-chat]]).

## Survey, in mechanism terms

Two outside projects were read on 2026-11-07. Neither lends code; the mechanisms below are what the plans adopt or reject.

**An autonomous coding desktop, open source.** The fork the owner pointed at stopped in March 2026; the original kept going and rewrote its memory:

- A work item is a plan on disk (phases and subtasks with a status); each session is a new process that reads the plan, takes the next item and writes the result; recovery per item (retry, skip, escalate). Adopted as the audit job (G4).
- After each session a cheap model extracts a fixed JSON from the diff, the task, the earlier attempts and the outcome: per file its purpose, change, patterns and gotchas; patterns and gotchas discovered; what worked or failed and why; recommendations. The rule: actionable knowledge, never logs. Adopted as the extractor (F1).
- The next session receives a fixed minimum: the file map, the patterns, the gotchas and the last three session records. Adopted as the context pack with budgets (F1).
- The memory moved from a graph database behind a Python sidecar to SQLite inside the app, with BM25 ranking, a context packer with a token budget per phase and per type that drops near-duplicates, a session observer that promotes signals to memories through an eight-stage filter, a trust gate that marks any signal produced after a web read as possibly injected, a dead-end detector, and injection between steps when a gotcha applies. Adopted: SQLite, full-text ranking (G3), the funnel, the trust gate and the step injection (F1). Rejected: the graph database.
- Ideation runs one read-only agent per dimension (code improvements, quality, documentation, performance, security, UI) with a fixed input (a deterministic project index, what exists and is planned, the memory's file map and patterns) and a fixed output (id, title, description, rationale, affected files, effort, approach, what it builds upon), deduplicated against the roadmap; a roadmap has a discovery agent and a features agent; an idea becomes a task with one click. Adopted as F2, with issues instead of tasks.
- A code graph from a parser per language with a closure table answers "what breaks if I change X". Deferred to F7.

**A commercial security audit agent.** Builds a threat model of the code first; a deep scan is several independent scans with workers that stop when nothing new appears or on a time limit, keeping partial results; each finding carries a validation status (from the source, or reproduced at run time), a root cause and an attack path; findings persist across scans and a dismissal keeps a written reason. Adopted as F6 without the run-time reproduction, because Scout never runs or edits the project's code; the stop rule and the partial result enter G4.

## The design both plans adopt

A project memory per repository in SQLite, which Scout already uses for the knowledge index and the meetings ([[pages/decisions/0037-meetings-kept-locally-in-sqlite]]):

| Part | Holds | Written by |
|---|---|---|
| Profile | the project's conventions | exists (`remember_project_conventions`, [[pages/services/plugin-github]]) |
| File map | path, one-line purpose, date | the extractor after each turn |
| Patterns and gotchas | one line each, where it applies | the extractor, and the model through `remember` |
| Session records | task, files read, outcome, recommendations | the extractor |
| Work items | job, item, status, result | Scout, per batch |
| Findings | id, severity, state (open, issue opened, dismissed with reason) | the cards |

The minimum sent with a turn is a pack with a fixed budget per part, about 12,000 characters in all: the profile, the file map entries relevant to the question, patterns and gotchas, the last three session records, the open items; the model asks for more with `recall`. The audit becomes work items generated from the snapshot's structure; each batch is a turn with a closed scope; after a batch the extractor writes and the next batch starts; findings are merged by path and title. Without embeddings the search is full-text; with them, semantic search on top, as today.

Open question for the ADRs: whether the extractor runs on the same provider as the turn or on a cheaper local model when one is connected ([[pages/invariants/local-first-default]] names the default).
