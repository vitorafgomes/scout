---
title: "Desktop UI: Theme and notices: Tests: ui/packages/renderer/src/styles.spec.ts (the base not dark,"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Desktop UI service page."
page_type: service
status: active
description: "Read when a change touches: Tests: ui/packages/renderer/src/styles.spec.ts (the base not dark,; Notification center."
source: - ui/packages/main/src - ui/packages/preload/src - ui/packages/renderer/src - ui/packages/shared/src - ui/build
reliability: high
---

## Theme and notices

Tests: `ui/packages/renderer/src/styles.spec.ts` (the base not dark, each scheme's pinned roles and literal tokens, the dark canvas, the system fallbacks equal to the chosen blocks, every text colour at 4.5:1 on its background in both schemes, including the pairs the chat and AI Sources use, the focus ring, the CSP-safe loader), `ui/packages/main/test/shell-theme.test.ts` (stored, absent, unknown and unreadable choices, each choice stored, applied and pushed, refusals, a vault that cannot store, closed windows, the system following, dispose, the `main.ts` wiring), `ui/packages/renderer/src/app/core/theme.store.spec.ts`, `ui/packages/renderer/src/app/core/icon.component.spec.ts`, `ui/packages/renderer/src/app/shell/top-bar.component.spec.ts` (the AI Source line, the labels, the bell, the switch's four starting states by three clicks, each accepted and refused, and the title rows), the top bar, icon, active-item and dark-canvas cases of the shell and operator shell specs (the post-connect shell's route matrix: every menu route, Local Models, a route outside the menu, the wizard pages, a query and a fragment, each themed or pinned, and Chat to Models and back; the operator shell's matrix: the five operator pages themed, Approvals pinned, a query and a fragment, Production to Approvals and back, and the fixed frame with the top bar outside the one scrolling area), the "chat follows the dark and light theme" cases of `ui/packages/renderer/src/app/chat/chat.component.spec.ts` and the "AI Sources follows the theme too" case of `ui/packages/renderer/src/app/sources/sources.component.spec.ts` (no hex, rgb() or hsl() colour left in their styles, the GitHub preview's light rules excepted and asserted light), `ui/packages/renderer/src/app/shell/shell.component.spec.ts` (before setup the wizard pages follow the theme and Slack and Settings stay pinned; after setup the frame follows it and the page area is decided per route), the "follows the theme and the prototype" cases of the Production, Assembly, Comments, Reports and Experiments specs and of each wizard page's spec and the step indicator's (no hex, rgb(), hsl() or named colour in their styles, the main actions drawn with their icon, Production's delete as an alert with Cancel focused, Assembly's video on the media tile), the per-window theme cases of `ui/packages/renderer/src/app/app.routes.spec.ts`, the Appearance block of `ui/packages/renderer/src/app/wizard/settings/settings.component.spec.ts`, `ui/packages/renderer/src/app/core/notice.service.spec.ts` and the toast tests in `ui/packages/renderer/src/app/features/features.component.spec.ts`. Screens migrate to Material components and the tokens one per version, keeping their `data-test` ids.

### Notification center
