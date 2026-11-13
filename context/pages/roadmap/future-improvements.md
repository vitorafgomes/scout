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
- **Out of scope:** anything on the gap plan; the Teams adapter, which keeps its own page. The live voice and meeting work enters as F11 to F18 from [[pages/roadmap/voice-meetings-spike]]; its earlier phases stay on [[pages/roadmap/live-assistant]].

## Target outcome

By 2027-02-28: Scout gives a verdict on a pull request, approve or request changes, with the project's tests run in isolation when the repository's CI does not run them; a question about a project the owner worked on last week is answered with that week's facts without a re-read; a repository's ideas appear as cards and a chosen idea opens an issue; the AI Sources screen shows credits or plan window per source; Claude answers through the same abstraction as the other providers.

## Status

- **As of 2026-11-07:** nothing started; F3's prototype is approved and its plumbing mapped; the YouTube operator's presenter lanes (F9) are interrupted and wait for the gap plan.
- **2026-11-08, F21:** the mention slice is done. The spike found that GitHub search does not find an App bot with `mentions:` but does find the phrase `"@<slug>"`, so the bell's poll searches it and confirms every hit from the pull request's body and comments; a confirmed mention is a bell entry with Review it, which asks the chat for the review; the slug is re-read from the App so a rename is followed, and the GitHub card shows the mention to copy ([[pages/decisions/0045-theme-and-notification-center]] v1.4, [[pages/history/desktop-github]]). Still open in F21: the shared review rules and the Adopt review instructions finding, the reply on the pull request when GitHub writes are granted, the requested-reviewer check, and the per-repository switch for new pull requests.
- **Next milestone:** the ADR for F1, written on top of G4's tables.
- **Blocker:** G3 and G4 on the gap plan.

## Items

| # | Item | Depends on |
|---|---|---|
| F1 | Project memory in SQLite: three layers (conversation per channel on disk with a summary of what each turn did and the files it read; working memory per task, which is G4's progress; long-term facts and decisions), `remember` and `recall` as functions for the model, an extractor after each turn that writes file purposes, patterns, gotchas and recommendations as structured records, a promotion funnel so a signal becomes a memory only after it holds, a trust gate that marks anything derived from a function result or a web page as data and never as a rule, injection between steps when a gotcha applies, a context pack with a fixed budget per part (profile, file map, patterns and gotchas, the last three session records, open items) of about 12,000 characters in all, an export to Markdown for the owner to read | G4 |
| F2 | Ideas and roadmap from a repository: the audit's mechanism with dimensions of opportunity instead of defect (what the code reveals: pattern extensions, architecture openings, configuration, utilities, UI, data handling, infrastructure), one turn per dimension with a fixed input (the snapshot's structure, what already exists and is already planned, the memory's file map and patterns) and a fixed output (id, title, description, rationale, affected files, effort, approach, what it builds upon), deduplicated against open findings and issues, shown as cards, a chosen idea opens an issue | G4, F1 |
| F3 | AI Sources cards with credits, plan window and Scout's usage per source, in three phases (the approved prototype; no existing function lost) | none |
| F4 | Claude as a provider through the API, and a spike on the owner's Claude subscription | F3 |
| F5 | The screens migrated to Angular Material one per version ([[pages/decisions/0041-angular-material-in-the-renderer]]); the audit against the approved artboards and the order of the versions in [[pages/roadmap/prototype-migration]] | none |
| F6 | The audit with validated findings: a threat model of the repository first, dimensions run as independent jobs that stop when nothing new appears, a validation status per finding, always from reading the source since Scout never runs the project's code, root cause and path, findings persisted across audits, a dismissal with its reason | G4 |
| F7 | A code graph over the snapshot for "what breaks if I change X": Roslyn for C#, a parser per other language, a closure table in SQLite | G3 |
| F8 | Feature credentials in the system keyring instead of memory ([[pages/roadmap/followups]], entry of 2026-10-23) | none |
| F9 | The YouTube channel operator's presenter: generated inside the scene with character consistency, then a dialogue with two presenters ([[pages/roadmap/youtube-channel-operator]]) | none |
| F10 | Moved to [[pages/roadmap/gap-plan]] G8 on the owner's word the same day ("our next deliverable has to do all of this"); kept here for the dependency graph. The verdict on a pull request: approve or request changes as a declared, per-action approved write beside the commenting review ([[pages/decisions/0040-review-findings-as-cards-in-chat]], [[pages/decisions/0032-per-feature-write-permissions]]); the project's tests as part of the verdict, first by reading the pull request's own check runs (a read), then, for a repository without a CI that runs them or when the review asks for one more check, by running the project's own test command inside a disposable container on the owner's machine: the stack detected from the manifests, the command from the CI workflow, the manifest or the conventions profile, the snapshot copied in, no secret of the owner's inside, no network after the dependencies are restored, an unprivileged user, a read-only filesystem but the work folder, CPU, memory and time caps, the container discarded at the end; without a container runtime the verdict says the tests did not run and why; the review card shows the tests line and the two verdict buttons. The owner's words (2026-11-07): "Scout has to be able to approve or reject a PR, that is the goal, to validate everything", "the user having a Docker where Scout runs everything isolated" | G4, F1 |
| F11 | Voice and meetings, quick wins: the screenshot encoding ladder under main's cap, one data-not-instructions sentence with an attached image, weekly provider canaries, a network allowlist generated from the declarations with a golden test, retired and replacement fields on the model catalog ([[pages/roadmap/voice-meetings-spike]]) | none |
| F12 | The `--talk` trigger from anywhere on Wayland: a desktop custom shortcut toggles a voice take in quick ask, since COSMIC has no GlobalShortcuts portal; a portal spike for GNOME and KDE ([[pages/roadmap/voice-meetings-spike]]) | none |
| F13 | Meeting detection and the start prompt: the Linux microphone-in-use detector offers a meeting and only calls `startMeeting`, never captures by itself ([[pages/roadmap/voice-meetings-spike]]) | none |
| F14 | Transcript quality: audio time on `heard` finals, then echo and duplicate handling (text-layer holdback and retract, then a correlation gate replacing the half-duplex zero-fill) ([[pages/roadmap/voice-meetings-spike]]) | none |
| F15 | Faster local transcription: Vulkan Whisper in-process with a reported CPU fallback, then CUDA, then a streaming transducer spike, measured on the owner's machine first ([[pages/roadmap/voice-meetings-spike]]) | none |
| F16 | Audio import into a kept meeting: a file the owner picks, ffmpeg to stdout so audio never touches disk, then direct URLs ([[pages/roadmap/voice-meetings-spike]]) | F14's audio time |
| F17 | Speaker labels on the system-audio lane, then named voice profiles behind an ADR on biometric data ([[pages/roadmap/voice-meetings-spike]]) | F14's audio time; an ADR for profiles |
| F18 | Scout as an MCP server over stdio, knowledge tools read-only; meeting tools only on the owner's grant ([[pages/roadmap/voice-meetings-spike]]) | F11's allowlist page |
| F19 | Docker as the one place Scout runs anything: beside a pull request's tests (ADR 0043), declared run kinds under the same container rules, such as building the project, running its linters and analyzers or a scanner without installing them on the host, and reproducing a reported bug before opening the issue; a command the model proposes runs only after the owner approves that exact command on a card; never on the host, never Scout's own code ([[pages/decisions/0047-the-docker-card]]) | G8's sandbox; an ADR for each run kind |
| F20 | Security hardening: Electron fuses and a sender check on the grant channels, functions scoped per skill and job, measured injection resistance, an evidence chain on security findings, Scout's threat model and an agent-safety review lens ([[pages/roadmap/security-hardening]]) | none |
| F21 | One set of rules for review and audit: Scout's own rules stay in the `code-review` and language skills; the per-repository part is the conventions profile `code-audit` already builds and saves, which `code-review` reads too. When the repository keeps instruction files for review or coding assistants, the profile follows them (read as data under Scout's rules). When it keeps none, Scout asks once in chat which assistant the team works with, keeps the answer in the profile, and yields one 'Adopt review instructions' finding, the same way `project-vault` yields 'Adopt a vault': an issue, opened only on the owner's approval, proposing the file that assistant reads (a cross-tool agents file when there is no preference) with content drawn from the conventions Scout found in the code; Scout never writes the file. Then a request for review by name only, so no team changes how it works: whoever opens or follows a pull request mentions the Scout bot in the description or a comment, by the name of the GitHub App the owner created (the creation screen suggests `scout-review`; App names are unique across GitHub, so another owner's bot takes a variant); no label. The poll watches the name of the owner's own bot, never a fixed one: the slug GitHub returned when the bot was created, which Scout already keeps, read again from the App itself at each start and when the GitHub card opens so a rename is followed; the GitHub card shows the current mention ready to copy. The bell's GitHub poll watches for it within its search budget while Scout runs, and a mention made while Scout was closed is found at the next start from the poll's last window; Scout prepares the review cards, announces in the bell who asked on which pull request, answers on the pull request that the review is being prepared only when the owner granted GitHub writes, and posts the review only after the owner approves. A spike first checks that GitHub search finds a mention of an App bot (if it does not, the poll reads the comments of the open pull requests in the repositories the owner granted) and confirms that an App cannot be a requested reviewer; on the owner's switch per repository, a new pull request may also prepare its cards without being asked | none |

Order: F1, F10, F3, F2, F5 interleaved one screen at a time, then F6 and F7 once the audit is in daily use; F8 and F9 when the owner asks. F10 is the owner's stated goal ("validate everything") and comes right after the memory it reads from. The voice and meeting items F11 to F18 come after F1 and F10, F11 first, then in their number order ([[pages/roadmap/voice-meetings-spike]], Proposed phase order).

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
