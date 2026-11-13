---
title: "Desktop operator window: Experiments"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Desktop operator window service page."
page_type: service
status: active
description: "Read when a change touches: Experiments."
source: - "context/pages/services/desktop-ui.md" - ui/packages/main/src - ui/packages/renderer/src/app/production - ui/packages/renderer/src/app/assembly - ui/packages/renderer/src/app/reports - ui/packages/renderer/src/app/experiments - ui/packages/renderer/src/app/comments - ui/build
reliability: high
---

## Experiments

The Experiments page (route `operator/experiments`, `ui/packages/renderer/src/app/app.routes.ts:106`; `ExperimentsComponent`, `ui/packages/renderer/src/app/experiments/experiments.component.ts:312`) lists the same published projects the same way, with the same loading, empty and failed-read states, the newest chosen and the owner's pick kept, and "Proposals in Reports" in its header, and shows, following the control plane's own rules ([[pages/services/control-plane]]) as `Experiments.dc.html` draws it:

- **Start a test** - "Hours per variant", 48 at first, whole hours from 24 to 168 (`:336`), with "24 to 168 hours" under it and "Each variant runs 24 to 168 hours." outside the range; "Start experiment" stays disabled outside that range, on a video that is not `published` or `scheduled` ("Only a published or scheduled video is tested; this one is <status>.", `:363`), while an experiment is `running` ("A test is already running on this video. Start another when it ends.", `:367`), and with no accepted title or thumbnail proposal ("Accept a title or thumbnail proposal on the Reports page first.", or, once a test has run, since a start marks its proposals `tested`, "The last test has ended. Accept another title or thumbnail proposal in Reports to test it."); otherwise a line names what it tests, 'Tests the accepted title "<value>" against the current one.'. It opens a Material dialog on the `sc-dialog` card, 200 px from the top with Cancel focused, "Start the <kind> test?", listing every action the experiment will create (`:166`, built at `:371`), each with its scope, `videos.update` for a title or `thumbnails.set` for a thumbnail (`:31`): 'Set the <kind> to "<current>", the current one, for <h> hours' for the control with the package's current title or thumbnail, then the first two accepted proposals of the first accepted kind, 'Change the <kind> to "<value>" for <h> hours', and last 'Put back "<current>" when the test ends'. Confirming sends `scout:operator.experiment` with the hours (`:400`) and closes it; a refusal keeps it open with its reason; Cancel, Esc and the scrim send nothing.
- **The test** (`:115`) - "<Kind> test" with its status chip (Running, Concluded, Inconclusive or Adopted), "Times in London", and a row per arm: "Current <kind>" for the control, else the arm's label, the value, start and end in London ("Not yet" when not yet), and, once the arm has ended, impressions, clicks, the click-through rate (clicks over impressions, as the worker computes it, to one decimal) and average view percentage as returned, "Not yet" before; the winning row on the ok pair with a "Winner" chip. Under the table, "How the winner is chosen" puts the worker's rule in words.
- **Result** (`:142`) - the result text as returned, which carries z and the click-through rates, on the ok pair with a winner and the warn pair without.
- **Adopt the winner** (`:149`) - only on a `concluded` experiment with a winner, the control plane's condition; its dialog says 'Scout changes the <kind> on YouTube for good to "<winner value>".' and lists the one action it creates with its scope (`:206`), 'Set the <kind> to "<winner value>"', and confirming sends `scout:operator.experimentAdoption` (`:413`).
