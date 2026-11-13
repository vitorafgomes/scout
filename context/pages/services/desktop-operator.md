---
title: "Desktop operator window"
version: "1.22"
date: 2026-11-04
updated: 2026-11-11
changes: "v1.22 (2026-11-11) Presenter versions by id: Use and Remove send the clicked version's id, the strip is keyed by it, main refuses a blank id, and a version the control plane no longer has reloads the project and says \\\"That version is no longer there; the list was refreshed.\\\"; citations into."
page_type: service
status: active
description: "The desktop's YouTube operator window: its Production, Assembly, Reports, Experiments and Comments pages over the control plane's operator state, the publish dialog, the scout-media previews, and the ffmpeg and fonts the desktop bundles for the worker."
source:
  - "context/pages/services/desktop-ui.md"
  - ui/packages/main/src
  - ui/packages/renderer/src/app/production
  - ui/packages/renderer/src/app/assembly
  - ui/packages/renderer/src/app/reports
  - ui/packages/renderer/src/app/experiments
  - ui/packages/renderer/src/app/comments
  - ui/build
reliability: high
---

# Desktop operator window

> The part of [[pages/services/desktop-ui]] where the owner runs the YouTube channel operator; the pipeline is the worker's ([[pages/services/operator]]) and its state the control plane's ([[pages/decisions/0033-channel-operator-architecture]]).

## Rules

The rules this project must keep, and what to read before changing it, are in `.claude/rules/desktop-operator.md`. Claude Code loads that file when a task touches the project.

## Topics

Read the page your change touches, not all of them.

| Page | Read when a change touches | Characters |
|---|---|---|
| [[pages/services/desktop-operator/operator-window\|Operator window]] | Operator window | 8548 |
| [[pages/services/desktop-operator/production-the-production-page-route-operator-pr\|Production: The Production page (route operator/production in]] | The Production page (route operator/production in; Channel; Mandate; What Scout can make; Make a video; Projects | 11417 |
| [[pages/services/desktop-operator/production-project-detail\|Production: Project detail]] | Project detail; Claims; Delegate; Delete | 8414 |
| [[pages/services/desktop-operator/production-presenter\|Production: Presenter]] | Presenter | 8922 |
| [[pages/services/desktop-operator/production-scenes\|Production: Scenes]] | Scenes | 13962 |
| [[pages/services/desktop-operator/production-background\|Production: Background]] | Background; Recording; Help marks; The screen reads the list on | 3648 |
| [[pages/services/desktop-operator/production-in-main-registeroperatorhandlers-ui-p\|Production: In main, registerOperatorHandlers (ui/packages/main/src/ipc/operator.ts:321, wired at]] | In main, registerOperatorHandlers (ui/packages/main/src/ipc/operator.ts:321, wired at | 10544 |
| [[pages/services/desktop-operator/production-owner-files\|Production: Owner files]] | Owner files; Previews | 5952 |
| [[pages/services/desktop-operator/assembly\|Assembly]] | Assembly | 10622 |
| [[pages/services/desktop-operator/reports\|Reports]] | Reports | 2993 |
| [[pages/services/desktop-operator/experiments\|Experiments]] | Experiments | 3797 |
| [[pages/services/desktop-operator/comments\|Comments]] | Comments | 8018 |
| [[pages/services/desktop-operator/media-tools-scout-desktop-ships-the-ffmpeg-the\|Media tools: Scout Desktop ships the ffmpeg the]] | Scout Desktop ships the ffmpeg the; Pinned table; Fetch; Piper; Rhubarb; Mouths; Voices; Face models; Presenters; Packaged; Worker environment; macOS; CI | 12682 |
| [[pages/services/desktop-operator/media-tools-licences\|Media tools: Licences]] | Licences | 885 |
| [[pages/services/desktop-operator/how-the-rules-are-kept\|How the rules are kept]] | the evidence for a rule of this project: the code and the tests that hold it | 3906 |
| [[pages/services/desktop-operator/tests-the-operator-window-is-covered-by\|Tests: The operator window is covered by]] | The operator window is covered by | 22625 |
