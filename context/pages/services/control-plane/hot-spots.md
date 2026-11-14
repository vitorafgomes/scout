---
title: "ControlPlane: Hot spots"
version: "1.1"
date: 2026-11-13
updated: 2026-11-14
changes: "v1.1 (2026-11-14): citations re-pointed."
page_type: service
status: active
description: "Read when a change touches: Hot spots."
source: ["src/Scout.ControlPlane/", "tests/Scout.ControlPlane.Tests/", "context/pages/decisions/0033-channel-operator-architecture.md"]
reliability: high
---

## Hot spots

- `src/Scout.ControlPlane/Providers/SystemProcessLauncher.cs:13`: process spawn, kill of the whole tree, cross-platform.
- `src/Scout.ControlPlane/Providers/OllamaProviderService.cs:318`: streamed pull progress.
- `src/Scout.ControlPlane/Features/FeaturesService.cs:93`: setup flow combining validation, persistence and activation.
- `src/Scout.ControlPlane/Features/FeaturesService.cs:263` and `:340`: the only two paths that grant a write; both need the owner's confirmation.
- `src/Scout.ControlPlane/WriteGate/WriteGateService.cs:317`: the only path through which the worker's write is allowed or queued; a new scope access value must be decided here.
- `src/Scout.ControlPlane/WriteGate/WriteGateService.cs:150` and `:269`: the one claim every approved action is handed out through and the one rejection of a claimed action; a new path that calls the queue directly skips the write-log lookup and can send a recorded write again, or ask the owner to.
- `src/Scout.ControlPlane/Operator/OperatorService.cs:310`: the worker's only way to change operator state besides the owner's chat edits, which go through `ViaChatAsync` (`:2379`) to the desktop's own methods; a new field on `OperatorProject`, `Scene` or `VideoPackage` must be decided here, in `SceneRules.MergeFromWorker` (`src/Scout.ControlPlane/Operator/SceneRules.cs:112`) or in `PackageRules.MergeFromWorker` and `PackageRules.Same` (`src/Scout.ControlPlane/Operator/PackageRules.cs:96`, `:143`) as the worker's to set or the owner's to keep; the save of a presenter job goes through `PresenterSaved` (`src/Scout.ControlPlane/Operator/OperatorService.cs:2780`) alone, which takes only the job's outcome.
- `src/Scout.ControlPlane/Operator/OperatorService.cs:1658`: the only way the worker gets an approved YouTube action; what it may claim and when is decided here and in `Due`.
- `src/Scout.ControlPlane/Operator/PublicationRules.cs:43`: a new field on `Publication`, `Proposal`, `Experiment` or `ExperimentArm` must be decided here as the worker's to set or the owner's to keep.
- `src/Scout.ControlPlane/Operator/CommentRules.cs:65`: the worker's only way to change the comments; a new field on `AudienceComment` or `AudienceIdea`, or a new status, must be decided here as the worker's to set or the owner's to keep.
- `src/Scout.ControlPlane/Operator/OperatorService.cs:2193`: the only place a project id becomes a path; it accepts 32 hex digits and nothing else.
