---
title: "ADR 0042 - The audit as a job with saved progress"
version: "1.0"
date: 2026-11-07
changes: "Initial ADR draft"
page_type: adr
status: proposed
description: "A whole-repository audit is a job Scout drives in batches with its progress, the files read and the findings saved in SQLite under the data folder, so it covers the repository across turns and restarts without asking the model to remember or the owner to say continue."
supersedes: ""
superseded_by: ""
source:
  - chat
  - src/Scout.Skills/skills/code-audit.md
  - src/Scout.Plugin.GitHub/GitHubPlugin.Findings.cs
  - src/Scout.Plugin.GitHub/RepositorySnapshotStore.cs
  - src/Scout.Operator/OperatorWorker.cs
reliability: high
---

# ADR 0042 - The audit as a job with saved progress

> A whole-repository audit is a job Scout drives, batch by batch, with its plan, its progress, the files read and the findings saved in SQLite under Scout's data folder; the model audits one closed scope per turn and never has to remember the last one.

## Status

- [x] Proposed
- [ ] Accepted
- [ ] Deprecated / superseded by

Updated: 2026-11-07. Written for [[pages/roadmap/gap-plan]] G4, on the diagnosis in [[pages/roadmap/project-memory-spike]]; waits for the owner's approval before any code.

## Context

On 2026-11-07 the owner asked, from the floating chat, for an audit of a real repository with many files. Scout returned two findings and stopped; asked to continue, it started from nothing. Three facts of the current design explain it, none a defect of the model:

- The audit is one turn. `code-audit` audits six dimensions in a single answer and, on a large repository, tells the model to stop and ask for an area (`src/Scout.Skills/skills/code-audit.md:22`). On the ChatGPT plan path the function loop allows 40 calls per turn (`src/Scout.Llm/Providers/ChatGptPlan/ChatGptPlanLlmProviderFactory.cs:106`, [[pages/services/llm]]) and the model stops well before.
- Without embeddings there is no project search: `search_project` and `find_symbol` answer that it is not available (`src/Scout.Knowledge/ProjectKnowledgeService.cs:24`, [[pages/services/knowledge]]), so the model reads whole files and exhausts its context. [[pages/roadmap/gap-plan]] G3 fixes this independently.
- The conversation memory keeps the question and the reply text only, in RAM, six turns, 8,000 characters, 30 idle minutes (`src/Scout.Conversation/Memory/ConversationMemoryOptions.cs:22`, [[pages/services/conversation]]); the files read and the findings are not in it, and the audit's reply is short by design because the cards carry the details ([[pages/decisions/0040-review-findings-as-cards-in-chat]] v1.2).

The owner's framing: the model will never know the project; what matters is kept on Scout's side, classified, with a defined minimum to send. Two precedents in the tree already do this for other work: the channel operator runs long jobs in a `BackgroundService` with its state outside the conversation and posts progress into the originating channel ([[pages/decisions/0033-channel-operator-architecture]], `src/Scout.Operator/OperatorWorker.cs:18`, `src/Scout.Conversation/Abstractions/IOriginNotifier.cs:7`); the meetings and the knowledge index keep their rows in SQLite under the data folder ([[pages/decisions/0037-meetings-kept-locally-in-sqlite]], `src/Scout.Knowledge/SqliteProjectKnowledgeStore.cs:17`).

## Decision

- **A job, not a turn.** "Audit owner/repo" starts a job. The turn that starts it answers at once with the plan (the areas, the file count, "first batch running") and ends. From then on an `AuditWorker : BackgroundService` in a new library `src/Scout.Audit` (with `tests/Scout.Audit.Tests`, referenced by `Scout.Host.Console`, the same shape as `Scout.Operator`) runs the batches one after another until the plan is done, the owner stops it, or Scout closes; on the next start it resumes from the first batch not done. "Continue" is never needed; "how is the audit going" is answered from the job's rows.
- **The plan comes from the snapshot, by Scout, not by the model.** The job takes the snapshot first ([[pages/decisions/0039-repository-snapshot-under-the-data-folder]], `src/Scout.Plugin.GitHub/RepositorySnapshotStore.cs:94`) and cuts its file list into batches deterministically: manifests, configuration and the top-level documentation first; then each source folder, split when it passes 40 files or 200,000 characters; tests and generated folders last; the snapshot's file filter rules apply. The order and the caps are `Scout:Audit` options with those defaults.
- **A batch is one model turn with a closed scope.** The worker opens a turn through the kernel with the audit skill's pass 2 as the instructions, the project's conventions profile, the batch's file list, the titles of the findings so far (so a repeat is not reported again), and the sentence that the scope is this list and nothing else. The functions are the existing reads (`get_file_contents`, `search_code`, `search_project`, `find_symbol`, `list_repository_files`) and `present_audit_findings` (`src/Scout.Plugin.GitHub/GitHubPlugin.Findings.cs:47`); no new function the model can call. When the turn ends the worker records which files the model read (from the function calls it made), merges the batch's findings into the job's by path and title, marks the batch done, and starts the next one. A turn that fails (a provider error, the plan's usage limit, a cancel) leaves the batch not done with the reason; the worker retries it once, then moves on and reports it as not covered.
- **The cards accumulate.** Each batch that found something posts its block into the originating conversation through the same seam the operator uses for progress (`IOriginNotifier`), carrying the `scout-findings` fence `present_audit_findings` attached in that turn (the attachment contract of [[pages/decisions/0040-review-findings-as-cards-in-chat]] v1.1, `src/Scout.Llm/Abstractions/TurnAttachments.cs:12`); the desktop renders a posted block exactly as a reply's and merges cards by finding id, so the owner sees the findings grow while the job runs, and chooses, edits and dismisses them as today. The last post carries the verdict and the scope paragraph: areas covered, files read, batches not covered and why.
- **The rows live in SQLite under the data folder.** One file, `audit.db`, beside the snapshots: `audit_job` (id, owner, repo, commit, started, finished, status: running, stopped, done), `audit_batch` (job, order, area, files, status, started, finished, files_read, failure), `audit_finding` (job, id, severity, title, path, line, why, fix, rule, state: open, issue opened with its URL, dismissed with its reason). The job's rows are the minimum project memory that [[pages/roadmap/future-improvements]] F1 extends; nothing about long-term memory is decided here.
- **Declared like the snapshot.** Starting a job writes under Scout's data folder, so the function that starts it is declared with `AddWriteFunction` on the `github` feature's `Scout's data folder` row, beside `snapshot_repository` (`src/Scout.Plugin.GitHub/GitHubServiceCollectionExtensions.cs:98`, [[pages/decisions/0032-per-feature-write-permissions]]); the owner's existing grant covers it. The job makes no call to GitHub beyond the snapshot's reads; issues are still opened only from the cards, through `create_issue`, one approval per issue as today. Stopping the job is a read-class function: it writes a status row and nothing leaves the process.

## Consequences

- **Better:** an audit covers the repository, in order, across as many turns as it needs, and survives a restart of Scout; the model never has to remember; the provider without embeddings still gets a closed list of files per turn; the owner sees progress and findings arrive instead of a stopped answer.
- **Better:** the job's rows are the first project memory, so F1 extends tables rather than inventing them.
- **Worse:** a long audit costs one turn per batch; on a paid provider the owner sees the cost grow with the repository. The plan the first turn answers names the batch count before any batch runs, and the owner can stop at any time.
- **Worse:** one more hosted worker and one more SQLite file to own; the same shape as the operator, so the same tests and runbooks apply.
- **Neutral:** `code-audit` stays the skill of the single-turn audit of one area the user names; the job reuses its pass 2 text for each batch, so one text describes the audit.

## Alternatives considered

- **Ask the model to continue.** Rejected: the memory holds two short sentences of the last turn, and sending the files read again costs the whole context; it is exactly what failed on 2026-11-07.
- **A bigger replay of the conversation memory.** Rejected: the context window, not the memory, is the limit; a job with closed scopes is the only shape that scales with the repository.
- **One turn per dimension, dimensions in parallel, stop when nothing new.** Deferred to [[pages/roadmap/future-improvements]] F6: it builds on the job and adds the validation status per finding.
- **A separate gRPC service and desktop page for the job.** Rejected for now: the chat's progress posts and the cards already show the job; a page comes when the owner asks for one.

## Enforcement

Tests to write with the change, in the words of the requirement:

- `tests/Scout.Audit.Tests`: the plan from a snapshot (manifests first, folders split at the caps, tests last); a batch turn gets exactly its file list and the earlier titles; findings merged by path and title; a failed batch retried once then reported as not covered; a job resumes from the first batch not done after a restart; stopping the job writes the status and makes no call.
- `tests/Scout.Plugin.GitHub.Tests`: the start function is declared on the data-folder row; the job never calls a write on GitHub.
- `tests/Scout.Host.Console.Tests`: the worker is registered and the audit library reaches the kernel through the same abstractions as the operator.
- `ui/`: a posted block renders as cards and merges by id with the cards already on screen (matrix spec, state x action).

## Related

- [[pages/roadmap/gap-plan]] G4; [[pages/roadmap/project-memory-spike]]; [[pages/roadmap/future-improvements]] F1 and F6
- [[pages/decisions/0040-review-findings-as-cards-in-chat]], [[pages/decisions/0039-repository-snapshot-under-the-data-folder]], [[pages/decisions/0033-channel-operator-architecture]], [[pages/decisions/0037-meetings-kept-locally-in-sqlite]], [[pages/decisions/0032-per-feature-write-permissions]]
- [[pages/services/skills]], [[pages/services/plugin-github]], [[pages/services/knowledge]], [[pages/services/conversation]]
