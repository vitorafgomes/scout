---
title: "ADR 0041 - Angular Material as the renderer's component library and theme"
version: "1.1"
date: 2026-11-06
updated: 2026-11-09
changes: "v1.1 (2026-11-09) amendment: Inter ships inside the app, from a pinned package, where the decision said the system stack."
page_type: adr
status: proposed
description: "Scout Desktop's renderer adopts Angular Material 22 and the CDK: one global theme built from the palette the owner approved on the 2026-11-06 prototypes, app-level CSS variables mapped to the Material system tokens, a snackbar service for notices, and the screens migrating to Material components one per version; no Tailwind, no Bootstrap, no icon font from the network."
supersedes: ""
superseded_by: ""
source:
  - chat
  - "ui/packages/renderer/src/styles.scss"
  - "ui/packages/renderer/src/app/core/notice.service.ts"
reliability: high
---

# ADR 0041 - Angular Material as the renderer's component library and theme

> The renderer takes Angular Material 22 and the CDK as its component library, a single global theme built from the approved palette with app-level aliases over the Material tokens, and a snackbar service for notices; each screen then migrates to Material components as its own change, keeping its `data-test` ids and its behaviour.

## Status

- [x] Proposed
- [ ] Accepted
- [ ] Deprecated / superseded by

Updated: 2026-11-06. Proposed on the owner's words of 2026-11-06, after asking what CSS the app uses ("what kind of CSS are we using?", "the point is I want to use Angular Material", "you can start the migration to Angular Material"). Merging the chore's pull request accepts it.

## Context

The renderer is Angular 22.2 on the application builder, zoneless, with no global stylesheet: every one of its 39 components carries its own CSS inline, with literal colours (91 distinct hex values; the same six repeated hundreds of times) and no shared variables ([[pages/services/desktop-ui]]). On 2026-11-06 three screens were redesigned from the owner's prototypes (the chat's review cards, [[pages/decisions/0040-review-findings-as-cards-in-chat]]; the Features cards; the Models Analysis tab), and each had to redeclare the same tokens. The owner asked for a library: Angular Material, with toasts like Bootstrap's and no need for Tailwind.

The page runs under a strict CSP (`style-src 'self' 'unsafe-inline'; font-src 'self'`, no CDN) and must stay offline ([[pages/architecture/electron-provisioning-ui]], [[pages/decisions/0004-web-stack-angular]]).

## Decision

- **Angular Material and the CDK, at the Angular version**, as renderer dependencies installed with the app; nothing loaded from a CDN. Icons stay inline SVG; no icon font.
- **One global theme** in `ui/packages/renderer/src/styles.scss`: `mat.theme()` with palettes built from the approved colours (primary from the accent, neutrals from the dark surface, the error), dark by default (`color-scheme: dark`), typography from the system stack the app already uses, density 0. A light variant stays possible under `data-theme="light"` and is not shipped as a toggle.
- **App-level aliases** on `:root`, named for the roles the hand-written screens use and mapped to Material's system tokens (`--sc-bg`, `--sc-surface`, `--sc-surface-2`, `--sc-line`, `--sc-fg`, `--sc-fg-2`, `--sc-muted`, `--sc-accent`, `--sc-error` over `--mat-sys-*`; `--sc-ok` and `--sc-warn` as literal values, since Material has no success or warning roles; `--sc-radius`, `--sc-radius-sm`), so a screen can drop its literals before it adopts Material components.
- **Notices through `NoticeService`** over `MatSnackBar` (`show(message, kind)`, `dismiss()`), the one way a screen tells the owner something happened; the first use is the Features screen's grant and revoke.
- **Screens migrate one per version** ([[pages/conventions/tooling]] and the owner's rule of one change per version): each migration swaps the hand-written card, details, chips, toggle, dialog and status line for `mat-card`, `mat-expansion-panel`, `mat-chip`, `mat-slide-toggle`, `MatDialog` and the snackbar, keeps every `data-test` id and every behaviour its specs pin, and is reviewed on its own. The chore changes no screen's look.
- **No Tailwind, no Bootstrap.** Bootstrap brings its own look and components that would fight the approved design; Tailwind's utilities would duplicate the theme the tokens already give.

## Amendment (2026-11-09): Inter ships inside the app

The decision above says "typography from the system stack the app already uses". The audit of [[pages/roadmap/prototype-migration]] (row S2) found that the stack names Inter first, that no machine without Inter installed ever drew it, and that every screen therefore set wider and rounder than its artboard. The owner decided on 2026-11-09 that Inter is shipped inside the app. The rest of the decision stands.

- **The font.** Inter's variable font, latin subset, one `woff2` file for every weight from 100 to 900 (the artboards use 400 to 700). The stack keeps its order and its fallbacks, `Inter, -apple-system, 'Segoe UI', sans-serif`: a character outside the subset, and any other script, is drawn by the system font.
- **The source.** The package `@fontsource-variable/inter`, pinned to an exact version in `ui/packages/renderer/package.json` and resolved through `ui/bun.lock` with its integrity hash. No font binary is committed. The stylesheet's one `@font-face` points at the file in the package and the build copies it into the renderer output under `media/`.
- **Offline.** Nothing is fetched at run time or at first launch. `font-src 'self'` stays as it is and no font CDN is named anywhere.
- **Licence.** Inter is under the SIL Open Font License 1.1. The build copies the package's licence text beside the font, `media/inter/LICENSE` in the renderer output, the way the caption fonts carry `OFL.txt` beside them.
- **Not shipped.** The italic file, the optical-size axis and the other subsets (latin-ext, cyrillic, greek, vietnamese). Each is one more `@font-face` and one more file if a screen needs it.
- **Cost.** 48,256 bytes for the font and 4,477 for the licence in the renderer output.
- **Enforcement.** `ui/packages/renderer/src/styles.spec.ts` (one `@font-face`, the Inter family, the pinned package's file, no remote URL, the licence asset) and `ui/e2e/tests/opens-on-chat.spec.ts` (in the built app `document.fonts` reports Inter loaded with the top bar and the bell panel on it, the font and its licence are in the built output).

## Consequences

- **Better:** one palette in one file; dark and light from the same tokens; dialogs, snackbars, menus, tabs, panels and form fields from a library the Angular team maintains at the same cadence as Angular; the three redesigned screens stop carrying their own token blocks.
- **Worse:** two more dependencies to keep at the Angular version on every upgrade; the first Material screen pays the learning cost of the Material 3 theming API; a Material component's default look needs theming to match the prototypes.
- **Neutral but noteworthy:** the renderer tests run Material components in jsdom through the CDK overlay; the production bundle grows by the components actually imported (tree-shaken), which does not matter in Electron.

## Alternatives considered

- **Tailwind** - utilities, no components; toasts, dialogs and panels would still be hand-written; templates grow long. Rejected by the owner once Material was on the table.
- **Bootstrap** - a complete look of its own and jQuery-era components; theming it to the approved design costs more than building on Material's tokens.
- **Only a global token stylesheet, no library** - the cheapest fix for the colours, and the first half of this decision; it leaves every dialog, toast and panel hand-written.
- **Status quo** - 39 components with their own CSS and literals; every new screen repeats the work.

## Enforcement

- `ui/packages/renderer/src/app/core/notice.service.spec.ts` - the snackbar opens from the Material overlay with the message and the kind class, and `dismiss` closes it.
- The stylesheet spec - every `--sc-*` alias is defined on the root and maps to a `--mat-sys-*` token or a literal.
- `ui/packages/renderer/src/app/features/features.component.spec.ts` - allowing and revoking writes shows the toast naming the feature; the existing write-permissions tests stay green.
- `bun run --cwd packages/renderer build` - the theme compiles with no warning; `bun run test` - every renderer spec green (no screen's look or behaviour changes in the chore).
- Convention page: [[pages/conventions/tooling]]. Service page: [[pages/services/desktop-ui]].

## Related

- Services affected: [[pages/services/desktop-ui]] (styles, the notice service), later every screen under `ui/packages/renderer/src/app/`.
- Related: [[pages/decisions/0004-web-stack-angular]], [[pages/decisions/0011-backend-frontend-grpc-uds]], [[pages/decisions/0040-review-findings-as-cards-in-chat]].

## Notes

The prototypes of 2026-11-06 (the chat, the Features screen, the AI Sources screen, the Models screen) are the design the theme reproduces: dark surfaces with a slight blue bias, the blue accent, pills in the three severities, 12 px radius on cards and 8 px on buttons, 44 px targets.
