---
title: "Desktop UI: Review: The screen loads both lists on"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Desktop UI service page."
page_type: service
status: active
description: "Read when a change touches: The screen loads both lists on; The look; Text; Loading; The count on Waiting for you; The reason field; The alert; Chips and code; History's table; Tests: the Approvals shows each state."
source: - ui/packages/main/src - ui/packages/preload/src - ui/packages/renderer/src - ui/packages/shared/src - ui/build
reliability: high
---

## Review

The screen loads both lists on open, reloads on `scout:actions.changed`, which main pushes to every window after an approval or a rejection (`ui/packages/main/src/main.ts:979`, `ui/packages/main/src/ipc/features.ts:204`), and every 15 s while it is open. A failed load shows the error in an alert with Try again (`ui/packages/renderer/src/app/review/review.component.ts:96`) and keeps the last lists; when the control plane did not answer the alert reads the prototype's "Could not load the approvals: the Scout control plane did not answer. Nothing was approved or rejected." (`:754`). The bell carries no approvals. Tests: `ui/packages/renderer/src/app/review/review.component.spec.ts` (a running action has left Waiting for you and its count and shows in History; a late click says Already decided and drops the action; a failed approval shows its reason; and the "Approvals keeps every decision and its outcome visible" block: an approved action waiting to run, Start Scout per worker state, deciding a second action, a double click, a rejection with its reason as neutral, Signed as, a first load that fails), `ui/packages/renderer/src/app/shell/post-connect-shell.component.spec.ts` ("the badge on Approvals"), and the cross-window rows of `ui/packages/renderer/src/app/core/approval-strip.component.spec.ts` ("once decided anywhere, no window offers Approve and the Approvals count drops", "the owner's approval logged with a reason is not the outcome").

**The look** follows the artboard `context/prototypes/desktop/Review.dc.html` ([[pages/conventions/desktop-prototype]], version 9 of [[pages/roadmap/prototype-migration]]) and paints only with the theme tokens:

- **Text** at 14 px, the artboard's base size, so the tabs, an action's summary, the empty sentence and the alert read at the size the artboard draws.

- **Loading.** Until the first read answers, the `status` area "Loading the approvals" holds the indeterminate progress bar and, under it, three skeleton cards (`ui/packages/renderer/src/app/review/review.component.ts:113`): each a card as an action's, with a title bar at 45 %, a line at 70 % and a meta bar at 30 % of its width on `--sc-surface-2`, hidden from a screen reader. They leave when the read answers or fails.

- **The count on Waiting for you** is a `span` inside the tab, a 20 px pill on the warn container pair, so the tab's own name carries it once ("Waiting for you 2") and nothing describes it a second time. It is not `mat-badge`: that directive creates the badge element itself, which cannot carry the `pending-count` id the specs and the end-to-end test read, and it draws the badge over a corner of its host, where the artboard sets it in the line after the label.

- **The reason field** is a native text input inside its label, "Reason (optional)" above a 36 px field with the placeholder "Saved with the decision in the history". `mat-form-field` is not adopted, pending the owner: its default is a 56 px field with a floating label inside the outline, and bringing it to the artboard's label above a 36 px field is theme work on its height and padding tokens that this version did not do.

- **The alert** keeps the error container and its line, with the sentence in `--sc-fg`.

- **Chips and code.** The two chips that say where an approved action stands, "Approved, waiting for Scout to run it" and "Running now", are on the info container pair with 4 px above and below; every other chip has 3 px. A target is set in the mono face at 13 px.

- **History's table** is at least 900 px wide inside its scrolling frame, and the headers and the time of a write stay on one line.

Tests: the "Approvals shows each state of the prototype" block of `ui/packages/renderer/src/app/review/review.component.spec.ts`, 41 cases. The count per number waiting, against the cards shown, in the main window and under the operator window's feature filter, and per failed read. The loading state while the read is out, when it answers and when it fails. The reason field and, per decision and per content of the field, the request sent. Approve and Reject disabled per action in flight, a second click on either while a decision runs sending nothing, and a decision the control plane fails leaving its reason, the action still offered and History as it was. An action offered only while its status is `pending`. The time printed being the one the action was requested at. One row per state of the artboard (Loaded, Loading, Empty, Error, the two Approved states, History) that names, for each tab, every `data-test` id the screen renders and the sentences it shows, so an id that should be absent in one state is one the same table requires in another. What each element is drawn with is read from the rule that applies to the mounted element, never from the source text. The times in the expected sentences are built from the fixture's instant in the zone of the machine, because the renderer specs run in worker threads, where a test cannot set the zone.
