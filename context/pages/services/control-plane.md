---
title: "ControlPlane"
version: "1.54"
date: 2026-10-21
updated: 2026-11-12
changes: "Started entries (2026-11-12, issue #323): RequestToRunAsync appends a started entry to the write log before it answers a reversible write allowed and answers its id, RequestAsync stays a pure decision for the in-process grant checks, RecordAsync appends the outcome against a started id and refuses."
page_type: service
status: active
description: "Domain logic behind the desktop UI: manage the local Ollama runtime and its models, keep the catalog, state, credentials and write grants of optional features, the approval queue and the write log, answer the worker's write requests, and keep the channel operator's mandate, projects, scenes, packages, publications, proposals and experiments, media directories, the worker's readiness, and the comments on the owner's channel with the owner's replies, holds and ideas."
source: ["src/Scout.ControlPlane/", "tests/Scout.ControlPlane.Tests/", "context/pages/decisions/0033-channel-operator-architecture.md"]
reliability: high
---

# ControlPlane

> What the desktop app asks Scout to do on the user's machine: run Ollama, pull models, store feature settings.

## Responsibility

Owns the provider and feature domain services, the write gate and the channel operator's state, with no transport. [[pages/services/host-control-plane]] exposes them over gRPC. It does not run in the chat host ([[pages/services/host-console]]); the chat host reaches it only through the WorkerGate service, as a gRPC client, and shares no code path with it.

## Rules

The rules this project must keep, and what to read before changing it, are in `.claude/rules/control-plane.md`. Claude Code loads that file when a task touches the project.

## Topics

Read the page your change touches, not all of them.

| Page | Read when a change touches | Characters |
|---|---|---|
| [[pages/services/control-plane/public-contract-entry-point\|Public contract: Entry point]] | Entry point; Providers; Features; Write declarations and grants; Actions | 9857 |
| [[pages/services/control-plane/public-contract-write-gate\|Public contract: Write gate]] | Write gate; Owner controls | 7255 |
| [[pages/services/control-plane/public-contract-operator\|Public contract: Operator]] | Operator; Comments; Configuration keys; External boundaries | 12234 |
| [[pages/services/control-plane/how-it-works-mermaid\|How it works: mermaid]] | mermaid; The daemon is launched with both; Credentials and settings; Write grants; Approval queue and write log | 11371 |
| [[pages/services/control-plane/how-it-works-write-gate\|How it works: Write gate]] | Write gate | 11020 |
| [[pages/services/control-plane/how-it-works-the-owner-s-decisions\|How it works: The owner's decisions]] | The owner's decisions | 9769 |
| [[pages/services/control-plane/how-it-works-the-findings-card-s-issues\|How it works: The findings card's issues]] | The findings card's issues; Operator state | 8424 |
| [[pages/services/control-plane/how-it-works-the-desktop-side-getmandateasync-an\|How it works: The desktop side: GetMandateAsync answers the]] | The desktop side: GetMandateAsync answers the; The worker side: SaveProjectFromWorkerAsync, GetProjectForWorkerAsync, ListProjectsForWorkerAsync | 10577 |
| [[pages/services/control-plane/how-it-works-production-and-scenes\|How it works: Production and scenes]] | Production and scenes | 12880 |
| [[pages/services/control-plane/how-it-works-the-animated-character\|How it works: The animated character]] | The animated character | 6414 |
| [[pages/services/control-plane/how-it-works-the-automatic-presenter\|How it works: The automatic presenter]] | The automatic presenter; RequestPresenterAsync(projectId, feedback, prompt) (src/Scout.ControlPlane/Operator/OperatorService.cs:2601) cleans both | 11267 |
| [[pages/services/control-plane/how-it-works-removepresenterversionasync-removes\|How it works: RemovePresenterVersionAsync(projectId, versionId) removes a presenter version]] | RemovePresenterVersionAsync(projectId, versionId) removes a presenter version; A media file name is a; A worker save checks the scenes; The owner's edits from the chat | 11190 |
| [[pages/services/control-plane/how-it-works-deletion\|How it works: Deletion]] | Deletion; Assembly and the package; A worker save that carries a; The owner's package edits need a; An owner scene edit that changes | 11258 |
| [[pages/services/control-plane/how-it-works-delegation\|How it works: Delegation]] | Delegation; Publication; Proposals and experiments | 9875 |
| [[pages/services/control-plane/how-it-works-approved-actions-and-the-claim\|How it works: Approved actions and the claim]] | Approved actions and the claim | 10364 |
| [[pages/services/control-plane/how-it-works-the-worker-s-publication-saves\|How it works: The worker's publication saves]] | The worker's publication saves; Old research and the disconnect purge; Comments | 11732 |
| [[pages/services/control-plane/how-it-works-the-worker-s-comment-saves\|How it works: The worker's comment saves]] | The worker's comment saves; Comment actions; Readiness | 6657 |
| [[pages/services/control-plane/how-the-rules-are-kept-pages-decisions-0032-per\|How the rules are kept: pages/decisions/0032-per-feature-write-permissions: the write gate refuses an]] | evidence for: ADR 0032 - Features declare what they write, and the owner grants each one; ADR 0033 - Channel operator architecture: state in the control plane, pipeline in Scout.Operator, media  | 11736 |
| [[pages/services/control-plane/how-the-rules-are-kept-pages-invariants-no-execu\|How the rules are kept: pages/invariants/no-executive-writes: start, stop, pull and delete]] | evidence for: Ollama is the default LLM provider; Scout performs descriptive writes only; Secrets never live in source | 7847 |
| [[pages/services/control-plane/hot-spots\|Hot spots]] | Hot spots | 2816 |
| [[pages/services/control-plane/testing-footprint\|Testing footprint]] | Testing footprint | 4035 |

## Related

Direct project references: none. Referenced by [[pages/services/host-control-plane]]. Map: [[pages/architecture/project-map]]. What changed and when: [[pages/history/control-plane]].
