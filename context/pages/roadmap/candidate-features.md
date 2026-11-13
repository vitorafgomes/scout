---
title: "Candidate features: what Scout could take on later"
version: "1.0"
date: 2026-11-07
changes: "Initial candidate list"
page_type: roadmap
status: active
description: "Features seen in the projects surveyed on 2026-11-07, and ideas that came up while building the gap plan, kept as candidates: each with its fit with Scout's purpose, the writes it would need, what it depends on and a rough size. None is committed; the owner picks."
source:
  - chat
  - pages/roadmap/project-memory-spike.md
  - pages/roadmap/voice-meetings-spike.md
reliability: medium
---

# Candidate features: what Scout could take on later

> A list to choose from, not a plan. The owner, 2026-11-07: "create another roadmap with all the other features, aiming at a future change, in case we could have some of them; create a roadmap with possible changes we could implement in the future."

What is decided and ordered lives on [[pages/roadmap/gap-plan]] (what blocks real use) and [[pages/roadmap/future-improvements]] (F1 to F18). This page holds everything else worth remembering: the features of the projects surveyed in [[pages/roadmap/project-memory-spike]] and [[pages/roadmap/voice-meetings-spike]] that no plan took, and ideas that came up while building. A candidate moves to the future plan, with an F number and an ADR, only on the owner's word.

## Rules every candidate keeps

- **Purpose.** Scout creates information in repositories: it answers, reviews pull requests, gives verdicts, opens issues and comments, and runs a project's tests only in an isolated container; it never writes or edits code ([[pages/overview/vision]]). A candidate that would change code, or act outside Scout's own folders without a declared write, is marked "outside the purpose" and stays here until the owner decides.
- **Writes.** Every call that leaves the process is declared by its feature and runs under the owner's grant; an irreversible one is approved per action ([[pages/decisions/0032-per-feature-write-permissions]]).
- **Any provider.** The same feature on a local model and on a paid one ([[pages/invariants/local-first-default]]).
- **Our own code.** Another project may lend an idea, described by mechanism; never its code or its name.

Size: S a day or two, M about a week, L more than a week. "Purpose" is in, edge (needs a decision) or out.

## Repository work: reviews, issues, triage

| # | Candidate | What it does | Purpose | Writes | Depends on | Size |
|---|---|---|---|---|---|---|
| C1 | Duplicate issue detection | Before opening an issue, and on request for an existing one, search the repository's open and closed issues for the same problem and link them instead of opening a duplicate | in | comment on issue | F1 (memory) helps | M |
| C2 | Issue triage | For a new issue: a summary, the likely area and files, a severity, suggested labels shown as a card the owner applies | in | label an issue (new write, per action) | the snapshot, G3 search | M |
| C3 | Issue investigation | Asked about an issue, read the code it points at and comment with the likely cause, the files involved and how to reproduce, never a fix in code | in | comment on issue | G3, F7 helps | M |
| C4 | Pull request follow-up | When a pull request Scout reviewed gets new commits, review only what changed since its last review and say which earlier findings are resolved | in | review, comment | G8, F1 | M |
| C5 | Finding validator | Before a finding reaches the cards, a second pass re-reads the cited lines and drops findings the code does not support, saying how many were dropped | in | none | G4, G8 | S-M |
| C6 | Codebase fit check | A review dimension that judges whether a pull request follows the project's own conventions profile, separate from correctness | in | review | the conventions profile | S |
| C7 | Release notes from merged work | For a tag or a range, a changelog grouped by kind from the merged pull requests and closed issues, posted as a draft release or a comment | edge (a release is a new write) | draft release, per action | none | M |
| C8 | Import issues into chat | List and filter the repository's issues in the desktop as cards the owner can ask Scout to investigate, triage or comment on | in | none | C2, C3 | M |
| C9 | GitLab and Azure DevOps | The same review, verdict, comment and issue features through a second and third source-control plugin | in | as GitHub | the plugin abstraction | L each |

## Ideas and planning

| # | Candidate | What it does | Purpose | Writes | Depends on | Size |
|---|---|---|---|---|---|---|
| C10 | Ideas by dimension | Beyond F2's code improvements: separate passes for code quality, documentation, performance, security and UI, each with a fixed output (title, rationale, affected files, effort), shown as cards that become issues | in | open issue | F2 | M |
| C11 | Roadmap discovery | Read a repository and describe its purpose, its users, its current state and its constraints as a page the owner corrects, the input for C10 and F2 | in | none (Scout's data folder) | the snapshot | M |
| C12 | Competitor notes | From names the owner gives, a short comparison of what similar products offer, kept beside the roadmap discovery | edge (web reads) | none | C11, a web read feature | M |
| C13 | Effort and priority on ideas | Each idea carries an effort level and a reason, and the cards sort by value over effort | in | none | C10 | S |

## Audit depth

| # | Candidate | What it does | Purpose | Writes | Depends on | Size |
|---|---|---|---|---|---|---|
| C14 | Threat model first | Before a security audit, describe the repository's entry points, trust boundaries and assets, and audit against that model | in | none | G4 | M |
| C15 | Dimensions in parallel, stop when nothing new | Run the audit's dimensions as separate jobs and stop each one when a pass finds nothing new | in | none | G4, F6 | M |
| C16 | Dismissal with a reason, kept across audits | A dismissed finding keeps the owner's reason and never comes back in a later audit of the same repository | in | none | G4 | S |
| C17 | A coverage report | After an audit, a page of what was read, what was partly read and what was not, by area, the owner can keep | in | none (Scout's data folder) | G4 | S |

## Memory and context

| # | Candidate | What it does | Purpose | Writes | Depends on | Size |
|---|---|---|---|---|---|---|
| C18 | Dead-end memory | When a turn abandons an approach, remember it so a later turn on the same project does not try it again | in | none | F1 | M |
| C19 | Memory between steps | Inject a remembered gotcha in the middle of a long task when the files it names come up, not only at the start | in | none | F1 | M |
| C20 | A memory page the owner edits | The project memory shown in the desktop, each entry with its source turn, editable and deletable | in | none | F1 | M |
| C21 | Identifier-aware text search | A tokenizer that splits camelCase and snake_case, so "discount" finds `ApplyDiscount` (followups, 2026-11-07) | in | none | G3 | S |

## Desktop and voice

| # | Candidate | What it does | Purpose | Writes | Depends on | Size |
|---|---|---|---|---|---|---|
| C22 | A tasks board | A board of what Scout is doing and has done (reviews, verdicts, audits, issues opened), each card linking to its result | in | none | G4, G8 | M |
| C23 | Read the answer aloud | A button on a reply that speaks it with the connected source's speech or the local voice | in | none | the operator's speech | S |
| C24 | Ask with the selected text | A shortcut that sends the text selected in another app to Scout as the question's context, read only | edge (reading another app's selection) | none | F12 | M |
| C25 | Meeting notes actions | On a kept meeting: a summary, decisions, action items, and "open these as issues" through the cards | in | open issue | meetings, F14 | M |
| C26 | Custom vocabulary | Names and terms the owner adds so transcription spells them right | in | none (Scout's data folder) | F15 | S |
| C27 | Insights per meeting and per repository | Counts and trends the owner can read: reviews given, findings opened and closed, time in meetings | in | none | C22 | M |

## Integrations and reach

| # | Candidate | What it does | Purpose | Writes | Depends on | Size |
|---|---|---|---|---|---|---|
| C28 | Calendar link for meetings | Read the owner's calendar to name a meeting and remind before it starts | edge (a new read-only integration) | none | F13 | M |
| C29 | A public read API | A local, token-protected endpoint other tools call to ask Scout or read its findings | in | none | F18 | M |
| C30 | Workspace policy | A file an organisation ships that fixes which features, providers and writes are allowed, for teams that install Scout centrally | in | none | the feature catalog | M |
| C31 | Managed provider keys | Use a company's centrally managed provider access instead of a key on each machine | in | none | the provider abstraction | L |

## Outside the purpose (kept so the owner can decide)

| # | Candidate | Why it is outside | What would make it fit |
|---|---|---|---|
| X1 | Dictate into any app and paste | Writes into another application, outside Scout's folders | An ADR that declares typing into the focused app as a write the owner grants per use |
| X2 | Dictation translation hotkey | Same as X1 | Same as X1 |
| X3 | Edit the selected text in place | Same as X1, and it changes the owner's documents | Same as X1 |
| X4 | Notes with team spaces and web sharing | Needs a hosted service and accounts; Scout runs on the owner's machine | A separate product decision |
| X5 | Write and merge code (fixes, pull requests with code, automatic merge) | Scout never writes or edits code | Not planned |
| X6 | A kanban that builds features end to end | Same as X5 | Not planned |

## How a candidate is picked

The owner names it; it gets an F number on [[pages/roadmap/future-improvements]], an ADR when it adds a write, a provider call or a new store, and then the usual pipeline. A candidate dropped for good is moved to the bottom of this page with the date and the reason, not deleted.
