---
title: "Descriptive vs executive writes - refining the read-only invariant"
version: "1.0"
date: 2026-05-23
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: adr
status: accepted
description: "Refines Scout's read-only invariant by distinguishing descriptive writes from executive writes."
source:
  - "obsidian-vault/scout/06-decisions/2026-05-23-descriptive-vs-executive-writes.md"
reliability: medium
---

# Descriptive vs executive writes - refining the read-only invariant

## Context

Until now Scout's CLAUDE.md has stated the read-only invariant as:

> No plugin, adapter, or integration is allowed to perform write
> operations against external systems (no issue creation, no message
> posting outside the originating chat, no git writes, no cloud
> mutations). This is a security invariant, not a preference.

This was correct for the original scope: a chatops assistant that
reads logs, surfaces context, and never mutates the environment. As
the plugin ecosystem grows (GitHub, Kubernetes, future integrations),
this single-bar rule conflates two very different kinds of writes:

1. Writes that **describe work** to be done by humans or other agents
   — a GitHub issue, a PR comment, a TODO line.
2. Writes that **execute work** themselves — a code commit, a kubectl
   apply, a merge, a deploy, a database mutation.

The first kind is materially safer: an issue created by Scout cannot
break production on its own. It is a description of intent that
another agent (a human reviewer, a CI pipeline, a developer) decides
whether to act on. The second kind is the original target of the
invariant: actions that, once taken, cannot be intercepted by a human
in the loop without explicit rollback.

The first feature triggering this refinement is the planned GitHub
plugin (see Features screen), which needs to allow `CreateIssue` so
the user can ask Scout to file a backlog item from chat.

## Decision

Scout's external-system writes are partitioned into two classes:

### Descriptive writes — **PERMITTED**

A descriptive write produces content that another agent must later
**read and decide to act on**. It does not, by itself, mutate code,
configuration, infrastructure, data, or any executable state.

Examples permitted under this class:

- Creating a GitHub issue (title, body, labels)
- Posting a comment on a GitHub PR or issue
- Adding a label to an existing issue when that label is purely
  organisational (e.g. `triage`, `bug`)
- Posting a message in the originating chat channel (already
  permitted by the chat adapter contract)

### Executive writes — **FORBIDDEN**

An executive write mutates code, configuration, infrastructure, or
data in a way that takes effect immediately without a human gate.

Examples forbidden under this class:

- Committing or pushing code to any branch
- Merging, closing, or approving a PR
- Editing repository settings, webhooks, secrets, branch protection
- `kubectl apply`, `delete`, `exec`, `scale`, `patch`, `rollout`,
  `cordon`, `drain`
- Any write against cloud APIs (S3 put, RDS modify, Lambda update,
  IAM mutation)
- Database `INSERT`, `UPDATE`, `DELETE`, `DDL`
- Modifying files on the host filesystem outside Scout's own config
  directory
- Sending email or external notifications outside the originating
  chat

### Boundary cases

When unsure, a plugin author must default to **forbidden** and seek
explicit approval. The class of a write is determined by its
**effect**, not by the API surface:

- Creating a GitHub issue with a label that triggers a CI workflow is
  **executive**, not descriptive, because it indirectly mutates state.
  Plugin authors must surface this risk and require explicit user
  consent per repository.
- Creating a Kubernetes ConfigMap is **executive**, even though it
  resembles "just text" — it mutates cluster state.
- Adding a comment to a PR is **descriptive** even though it is a
  write, because the comment is content for humans, not an instruction
  for machines.

## Consequences

### Positive

- Unlocks high-value plugin functions (`CreateIssue`,
  `CommentOnPullRequest`) without breaking the safety guarantee that
  matters most to the user (Scout never breaks production by itself).
- Establishes a vocabulary plugin authors can use to classify their
  own write paths during design review.
- Aligns the invariant with how the user actually thinks about Scout:
  "describe work, never execute it".

### Negative

- The line is conceptually sharper than enforceable. A descriptive
  write that mentions credentials in plain text is still a leak even
  if it does not mutate state. Plugin authors must still treat content
  hygiene (no secrets in bodies, no PII unless explicitly opted in)
  as a separate concern.
- Requires per-plugin review at design time. There is no automated
  classifier — each new plugin function must be tagged
  `descriptive` or `executive` in code review, and PRs that introduce
  executive writes must be rejected.

### Operational

- The Features screen (see Phase 1 of the Features feature) ships
  with a per-plugin badge indicating which classes of write each
  plugin is allowed to perform. A future change of class for any
  existing plugin function requires a new ADR.
- `Scout.Plugin.GitHub` is permitted descriptive writes
  (`CreateIssue`, future `CommentOnPullRequest`, future
  `AddIssueLabel`) and forbidden executive writes
  (`MergePullRequest`, `CreateBranch`, `PushCommit`, repo settings,
  webhooks).
- `Scout.Plugin.Kubernetes` remains **strict read-only** for the
  foreseeable future. There is no scenario where Scout describing
  Kubernetes state via a write is safer than the user opening kubectl
  themselves. Any plugin author proposing a Kubernetes write
  (descriptive or otherwise) must produce a separate ADR.

## References

- Scout project rules: `obsidian-vault/scout/CLAUDE.md` (the
  invariant statement is being updated to reference this ADR).
- Features screen plan: this ADR triggers Phase 1–5 of the Features
  feature in active milestone.
- Original invariant statement: predates this vault; first codified
  in the bootstrap commit `chore: bootstrap repository`.
