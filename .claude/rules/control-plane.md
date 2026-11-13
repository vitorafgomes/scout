---
paths:
  - "src/Scout.ControlPlane/**"
  - "tests/Scout.ControlPlane.Tests/**"
---

# ControlPlane

Owns the provider and feature domain services, the write gate and the channel operator's state, with no transport. [[pages/services/host-control-plane]] exposes them over gRPC. It does not run in the chat host ([[pages/services/host-console]]); the chat host reaches it only through the WorkerGate service, as a gRPC client, and shares no code path with it.

## Rules this project must keep

- **ADR 0032 - Features declare what they write, and the owner grants each one.** Here: the write gate refuses an undeclared scope and a read row even under the grant, refuses a declared write without the grant, allows a reversible write under it, queues an irreversible one for the owner, reads the grant live, and records a write with an action id only once the owner approved that action for that scope.
- **ADR 0033 - Channel operator architecture: state in the control plane, pipeline in Scout.Operator, media through the connected AI Source, progress back to the originating channel.** Here: the worker's operator writes and reads refuse without the grant and leave a queued project queued, and so does the desktop's start and production, and every one of the owner's edits from the chat, which stores and logs nothing without the grant and logs each change that changed the project via chat.
- **Scout performs descriptive writes only.** Here: start, stop, pull and delete mutate the host; since 2026-11-11 they are ruled a reviewed local write on the owner's click, outside the feature grant, reached by the desktop token only, with the download's and the delete's log lines above ([[pages/decisions/0032-per-feature-write-permissions]], amendment of 2026-11-11).
- **Secrets never live in source.** Here: credentials go to `ICredentialStore` (in memory by default), not to the JSON state file, whose record holds only `Enabled`, `LastError`, `Settings`, `Granted` and `GrantedAt`; the queue and the log hold no secret either.
- **Ollama is the default LLM provider.** Here: the default endpoint is localhost.

The full statement of each, with the code and tests that hold it: `context/pages/services/control-plane/how-the-rules-are-kept-pages-decisions-0032-per.md`, `context/pages/services/control-plane/how-the-rules-are-kept-pages-invariants-no-execu.md`.

## Pages that govern this project

Read one when your change touches what its title names.

- [[pages/invariants/no-executive-writes|Scout performs descriptive writes only]]
- [[pages/decisions/0032-per-feature-write-permissions|ADR 0032 - Features declare what they write, and the owner grants each one]]
- [[pages/decisions/0033-channel-operator-architecture|ADR 0033 - Channel operator architecture: state in the control plane, pipeline in Scout.Operator, media through the connected AI Source, progress back to the originating channel]]
- [[pages/decisions/0035-screenshot-attached-to-a-question|ADR 0035 - A screenshot attached to a question: one JPEG on the ask line, an image part for a provider that can see, a plain sentence otherwise]] - the desktop feature's reads
- [[pages/invariants/secrets-never-in-source|Secrets never live in source]]
- [[pages/invariants/local-first-default|Ollama is the default LLM provider]]
- [[pages/services/host-control-plane|Host.ControlPlane]]
- [[pages/services/control-plane-contracts|ControlPlane.Contracts]]
- [[pages/conventions/testing|Testing]]

## What to read for a change

Find the row your change matches and read that page. Read one or two rows, not the table.

| If the change touches | Read |
|---|---|
| Entry point; Providers; Features; Write declarations and grants; Actions | `context/pages/services/control-plane/public-contract-entry-point.md` |
| Write gate; Owner controls | `context/pages/services/control-plane/public-contract-write-gate.md` |
| Operator; Comments; Configuration keys; External boundaries | `context/pages/services/control-plane/public-contract-operator.md` |
| mermaid; The daemon is launched with both; Credentials and settings; Write grants; Approval queue and write log | `context/pages/services/control-plane/how-it-works-mermaid.md` |
| Write gate | `context/pages/services/control-plane/how-it-works-write-gate.md` |
| The owner's decisions | `context/pages/services/control-plane/how-it-works-the-owner-s-decisions.md` |
| The findings card's issues; Operator state | `context/pages/services/control-plane/how-it-works-the-findings-card-s-issues.md` |
| The desktop side: GetMandateAsync answers the; The worker side: SaveProjectFromWorkerAsync, GetProjectForWorkerAsync, ListProjectsForWorkerAsync | `context/pages/services/control-plane/how-it-works-the-desktop-side-getmandateasync-an.md` |
| Production and scenes | `context/pages/services/control-plane/how-it-works-production-and-scenes.md` |
| The animated character | `context/pages/services/control-plane/how-it-works-the-animated-character.md` |
| The automatic presenter; RequestPresenterAsync(projectId, feedback, prompt) (src/Scout.ControlPlane/Operator/OperatorService.cs:2601) cleans both | `context/pages/services/control-plane/how-it-works-the-automatic-presenter.md` |
| RemovePresenterVersionAsync(projectId, versionId) removes a presenter version; A media file name is a; A worker save checks the scenes; The owner's edits from the chat | `context/pages/services/control-plane/how-it-works-removepresenterversionasync-removes.md` |
| Deletion; Assembly and the package; A worker save that carries a; The owner's package edits need a; An owner scene edit that changes | `context/pages/services/control-plane/how-it-works-deletion.md` |
| Delegation; Publication; Proposals and experiments | `context/pages/services/control-plane/how-it-works-delegation.md` |
| Approved actions and the claim | `context/pages/services/control-plane/how-it-works-approved-actions-and-the-claim.md` |
| The worker's publication saves; Old research and the disconnect purge; Comments | `context/pages/services/control-plane/how-it-works-the-worker-s-publication-saves.md` |
| The worker's comment saves; Comment actions; Readiness | `context/pages/services/control-plane/how-it-works-the-worker-s-comment-saves.md` |
| evidence for: ADR 0032 - Features declare what they write, and the owner grants each one; ADR 0033 - Channel operator architecture: state in the control plane, pipeline in Scout.Operator, media  | `context/pages/services/control-plane/how-the-rules-are-kept-pages-decisions-0032-per.md` |
| evidence for: Ollama is the default LLM provider; Scout performs descriptive writes only; Secrets never live in source | `context/pages/services/control-plane/how-the-rules-are-kept-pages-invariants-no-execu.md` |
| Hot spots | `context/pages/services/control-plane/hot-spots.md` |
| Testing footprint | `context/pages/services/control-plane/testing-footprint.md` |

For where a type lives or who calls it, ask the code graph (`graphify explain "<Type>"`, `graphify affected "<Type>"`).
