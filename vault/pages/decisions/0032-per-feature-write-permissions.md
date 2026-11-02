---
title: "ADR 0032 - Features declare what they write, and the owner grants each one"
version: "1.0"
date: 2026-11-02
changes: "Initial ADR"
page_type: adr
status: accepted
description: "Scout is no longer read-only by definition. Each feature declares the reads and writes it performs; the owner grants a feature's write permission in the desktop, irreversible actions ask for a per-action approval there, every write is logged, and a chat message or the model can never grant a permission or skip an approval. Supersedes the absolute prohibition of ADR 0009; keeps its classification."
supersedes: "0009-descriptive-vs-executive-writes"
superseded_by: ""
source:
  - chat
  - "src/Scout.ControlPlane/Features/FeatureMetadata.cs"
reliability: high
---

# ADR 0032 - Features declare what they write, and the owner grants each one

> A Scout feature says exactly which reads and which writes it performs; the owner turns each feature on in the desktop and, for a write that cannot be undone, approves each action there; the model and the chat can only ask, never grant.

## Status

- [ ] Proposed
- [x] Accepted
- [ ] Deprecated / superseded by

Updated: 2026-11-02. Accepted on the owner's decision while planning a YouTube channel operator: "antes faziamos so leitura, porem podes ter coisas que fazem leitura e escrita", "podemos ter diferentes features e cada uma tem uma permissao diferente", "criar conteudo para um canal tem que fazer muita coisa".

## Context

Scout was founded read-only: [[pages/decisions/0009-descriptive-vs-executive-writes]] permitted descriptive writes (content a human or agent reads and decides on, posted in the originating channel) and forbade executive writes (anything that mutates code, infrastructure, configuration, data or the host), with boundary cases defaulting to forbidden; [[pages/invariants/no-executive-writes]] carries the rule into the code and the review gate's `read-only` lens checks every change against it. That contract made the first plugins safe to ship fast: GitHub, Kubernetes and YouTube ([[pages/decisions/0030-youtube-plugin-read-only-with-browser-sign-in]]) are GET-only, and the only host writes so far are Scout's own configuration and its own installation ([[pages/decisions/0031-desktop-self-update]]).

The next product the owner wants is a channel operator in the shape of `darkzOGx/youtube-automation-agent`: research, script, narration, visuals, an assembled video, a review step, upload and scheduling on YouTube, comment handling and a learning loop over the channel's analytics. Most of that is production of files on the owner's machine and reads of YouTube; two steps are executive writes to an external system (upload, schedule) and more will follow (replying to comments, title and thumbnail experiments). A product that stops one step before publishing is not what the owner asked for.

The control plane already carries the seed of the answer: every feature in the catalog declares a `WriteClass` (`None`, `Descriptive`, `Executive`) and a scope matrix of the calls it makes, and the owner enables features one by one on the Features screen ([[pages/services/control-plane]], [[pages/services/desktop-ui]]). Until now every feature declared `None`.

## Decision

- **Scout is a chatops assistant whose features may read and write; each feature declares which.** The catalog entry of a feature is its contract: `WriteClass` (`None`, `Descriptive` or `Executive`) and a scope matrix with one row per call it makes, marked `read`, `write` or `write (irreversible)`. A feature that performs a write it did not declare is a bug the review gate rejects. Rule A of the team rules becomes: every call that leaves the process is declared by its feature; undeclared writes never ship.
- **The owner grants write permission per feature, in the desktop.** A feature with `WriteClass.Descriptive` or `Executive` starts disabled and shows its write rows on the Features screen before the owner enables it; enabling is the grant. Reads never need a grant beyond the credential the feature uses. The grant lives with the feature state in the control plane, never in a chat.
- **Irreversible writes ask for a per-action approval, in the desktop.** Uploading or scheduling a video, deleting anything, publishing a comment reply, changing a live title or thumbnail: each such action is queued with what will be done, and runs only after the owner approves it on a review screen of the desktop. The queue is visible, approvals are logged, and an approval covers one action. Reversible writes under a granted feature (a draft saved to Scout's own folder, a scheduled job the owner can cancel) run without a per-action prompt and are logged.
- **The model and the chat can only request.** No chat message, no system prompt and no function result can grant a permission or approve an action. A function the model can call that performs an executive write first checks that its feature holds the grant and, when the action is irreversible, enqueues it for approval instead of running it; the model is told the action is waiting for the owner. Scout's local quick-ask window counts as chat.
- **Every write is logged** with the feature, the action, the target, the time and who approved it, in Scout's own logs and on the desktop's Logs screen, and a feature's recent writes are visible on its card.
- **Credentials follow the grant.** A credential with write scopes (a YouTube token with `youtube.upload`, a GitHub token that can write) is requested only when a feature that needs it is granted, with the scope named on the card; a read-only feature keeps read-only scopes. The owner allow-list ([[pages/decisions/0021-allowed-users]]) still gates who can make Scout act, and a write-capable feature answers only listed users.
- **The guard adapts.** The read-only guard in the LLM path ([[pages/services/llm]]) stops refusing write intents outright and instead refuses writes for which no granted feature exists, naming the feature to enable.
- **What stays forbidden.** Writes to the host filesystem outside Scout's own folders (a feature that produces files writes under Scout's data directory and offers an export the owner clicks); any write by a feature without the grant; any approval that did not come from the desktop; writes in a chat other than the originating channel; secrets in source ([[pages/invariants/secrets-never-in-source]]).

## Consequences

- **Better:** Scout can be the channel operator the owner wants, and later other write-capable features, without a new founding debate each time: the catalog entry says what a feature does, the owner decides per feature, and the review gate has a precise thing to check. The audit trail is part of the product.
- **Worse:** the project's one-line promise changes from "it never writes" to "every write is declared, granted and logged"; the README, the project site and the pipeline's `read-only` lens all need the new wording, and every existing page that cites the absolute rule needs a date and a pointer here. A granted feature with a bug can write where it should not; the per-action approval limits that to reversible writes, and the logs make it visible.
- **Neutral but noteworthy:** the three existing plugins keep `WriteClass.None` and change nothing. The desktop's self-update ([[pages/decisions/0031-desktop-self-update]]) and the own-configuration writes are unchanged and remain outside the feature grants.

## Alternatives considered

- **Keep Scout read-only and stop before upload** (the owner publishes from YouTube Studio). Rejected by the owner: "criar conteudo para um canal tem que fazer muita coisa".
- **One global switch "allow writes".** Rejected: it grants everything at once; the owner asked for a permission per feature.
- **Approval through the chat** (reply "yes" to publish). Rejected: anyone on the allow-list, or a prompt injection in a message Scout reads, could approve; the desktop is the only surface the owner alone controls.

## Enforcement

- `src/Scout.ControlPlane/Features/FeatureCatalog.cs`: every feature declares `WriteClass` and a scope matrix; `tests/Scout.ControlPlane.Tests/Features/FeatureCatalogTests.cs` pins each feature's rows; a feature with a write row and `WriteClass.None` fails the test.
- The control plane refuses `Enable` on a write-capable feature until the owner confirms the write rows (phase 1 of the operator plan adds the confirmation and its test).
- The approval queue and its log (phase 4 of the operator plan) with tests that an irreversible action never runs before an approval and that a chat message cannot approve.
- The review gate's `read-only` lens is renamed `writes` and checks that every call leaving the process matches a declared row and the grant and approval paths exist for executive ones (`.claude/skills/scout-pipeline/SKILL.md`, to be updated with the first write-capable feature).
- `scripts/check-vault-refs.sh` and `wiki-lint` on every page that cited the absolute rule.
