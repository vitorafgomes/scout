---
title: "Operator: Hot spots"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Operator service page."
page_type: service
status: active
description: "Read when a change touches: Hot spots."
source: ["src/Scout.Operator/", "tests/Scout.Operator.Tests/", "context/pages/decisions/0033-channel-operator-architecture.md", "chat"]
reliability: high
---

## Hot spots

- `src/Scout.Operator/OperatorPipeline.cs:480`: the claim merge is what keeps a save from being refused by the control plane's tamper check; a change to claim ids or statuses must keep decided claims byte for byte.
- `src/Scout.Operator/OperatorServiceCollectionExtensions.cs:57`: the declaration's plugin and function names must match the plugin, or the write runs ungated; `tests/Scout.Operator.Tests/YouTubeOperatorPluginTests.cs:180` fails when a name, a parameter or a summary argument drifts.
- `src/Scout.Operator/SearchBudget.cs:9`: the counter is per process; a worker restart starts a new count, so YouTube's own bucket is the hard limit. The picture counter of `VisualGenerator` is per process too, so a restart allows 40 more pictures that day.
- `src/Scout.Operator/FilterText.cs:14`: every text or path placed in a filter graph must go through `Escape`; a narration with a `,`, `;`, `[` or `'` would otherwise end the filter and start another.
- `src/Scout.Operator/Assembler.cs:575`: the segment key must cover everything that changes the rendered segment; a new input to a segment command that the key does not see makes a re-assembly reuse a stale segment. A presenter scene adds the presenter picture to the input files and the mouth box, the mouth track and the mouth set's version as its identity (`src/Scout.Operator/Assembler.cs:505`); a new input to the presenter branch, such as a second mouth style, must join that identity. The key leaves out the font's folder and the mouths folder, which the desktop mounts at a new place on every launch; another path that moves between launches must be left out the same way, or every segment renders again after a relaunch.
- `src/Scout.Operator/OperatorPipeline.cs:338`: every production and assembly save must start from a read made immediately before it; a save built from an older read carries a stale scene list after any owner edit, which the control plane refuses with `FAILED_PRECONDITION`. A save that still races an owner edit in the moment between that read and the save is refused and fails the run with the control plane's words.
- `src/Scout.Operator/FaceMouth.cs:55` to `:70`: the tensor names and the shapes the code assumes are those of the two model files Scout Desktop ships, checked against them under **FaceMouth acceptance** below; another model file must keep them, or every search answers null, logged once, and every presenter becomes a built-in one.
- `src/Scout.Operator/PresenterStage.cs:250`: the worker deletes only a dropped version's preview narration, by name; a new file of its own that a version owns, such as another audio format, must join the names it deletes, or it stays in the media folder, while the picture and the `.mp4` are the control plane's to delete.
