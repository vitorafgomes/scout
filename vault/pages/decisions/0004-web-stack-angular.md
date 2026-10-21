---
title: "ADR - Web stack is Angular"
version: "1.0"
date: 2026-04-22
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: adr
status: accepted
description: "Decision to use Angular for Scout's web surfaces."
source:
  - "obsidian-vault/scout/06-decisions/2026-04-22-web-stack-angular.md"
reliability: medium
---

# ADR - Web stack is Angular

## Status

Accepted — 2026-04-22

## Context

Scout will eventually need web surfaces:

- The Electron provisioning UI renderer
  (see [[pages/architecture/electron-provisioning-ui]]).
- Potentially a future admin dashboard, status page, or hosted control
  plane.

Without an explicit framework choice locked in early, contributors will
ship PRs using whichever web framework is convenient for them (React,
Vue, Svelte, Solid). Each additional framework multiplies the
onboarding cost, the build-tooling surface, the component-library
decisions, and the testing infrastructure. A single-framework rule now
is cheaper than untangling a mixed codebase later.

The project owner has already standardized on Angular across other
projects and wants Scout aligned. This ADR makes that choice explicit
and enforceable.

## Decision

**All web UI for Scout uses Angular** — latest stable (currently
**21.2.9** at the time of this ADR; track the latest stable in
`angular/angular` releases). The following conventions apply to every
Angular codepath in the project:

- **Signals as the reactivity model.** State flows through `signal()`,
  `computed()`, and `effect()`. No `BehaviorSubject`-only patterns for
  component state; RxJS is reserved for actual stream semantics
  (cancellation, debouncing, HTTP).
- **Standalone components only.** No `NgModule` boilerplate. New code
  uses `standalone: true` (the Angular 21 default) with explicit
  `imports` arrays.
- **New control flow.** Template syntax uses `@if`, `@for`, `@switch`,
  `@defer`. Structural directives (`*ngIf`, `*ngFor`, `*ngSwitch`) are
  forbidden in new code; existing ones (none yet) migrate on sight.
- **Zoneless change detection when feasible.** At minimum, avoid
  zone.js-only reactivity (do not rely on implicit change detection
  triggered by zone-patched async work). Prefer `provideZonelessChangeDetection()`
  as soon as the runtime allows it for the project.
- **Angular CLI (`ng`) for builds.** `ng build`, `ng test`, `ng
  generate` are the canonical commands. They are invoked through
  `bun run` scripts where the package manager boundary allows
  (see the toolchain constraint in
  [[pages/architecture/electron-provisioning-ui]]). The Angular
  CLI owns the bundler pipeline (currently esbuild under the hood);
  `bun build` is not used for the renderer bundle.

## Consequences

### Positive

- **Single stack, single onboarding path.** New contributors learn one
  framework, one CLI, one testing story.
- **Consistency with the owner's other projects.** Code review
  patterns, linter configs, and muscle memory carry across.
- **Clear rejection criterion in CI.** Any PR introducing React, Vue,
  Svelte, Solid, or any other web framework is automatically out of
  scope and can be closed on framework choice alone.
- **Signals-first means the code ages well.** Angular is explicitly
  moving toward a zoneless, Signals-based future; starting there
  avoids a migration later.

### Negative

- **Angular's npm-centric tooling may friction against Bun.** Some
  Angular schematics historically shell out to `npm`, and Angular's
  peer-dependency resolution assumes npm semantics.
  `TODO: verify ng + bun interop` against the current Angular 21.x
  and Bun releases; document any npm-only escape hatch (e.g. a
  `package-lock.json` shim) in the Electron provisioning note.
- **Angular ships a heavier runtime than Svelte or Solid.** For a
  desktop Electron renderer this is largely irrelevant (no cold
  network payload, no SEO budget), but it is a real cost for any
  eventual public-facing web surface.
- **Ecosystem narrower for Electron-specific Angular patterns.**
  React has broader community adoption inside Electron tutorials;
  Angular-in-Electron is less documented. Expect to write thin
  integration glue ourselves.
- **Contributors may still ship PRs using other frameworks.** CI and
  review must reject non-Angular web code explicitly — add a lint
  rule or a pre-PR check if drift appears.

## Alternatives considered

- **React.** Largest ecosystem, best Electron tutorial coverage,
  richest component library surface. Rejected: owner has standardized
  on Angular; adopting React here would fragment the owner's
  cross-project stack, and Scout does not need any React-specific
  library that Angular cannot match.
- **Vue.** Good DX, reasonable Electron support. Rejected on the
  same ecosystem-split grounds as React, with a smaller upside.
- **Svelte / SvelteKit.** Smallest runtime, most ergonomic template
  syntax. Rejected: the bundle-size advantage is irrelevant for an
  Electron desktop renderer (where payload is local and startup is
  dominated by Chromium), and the ecosystem-split cost dwarfs the
  marginal gain.
- **Solid.** Signals-first by design, fast, small. Rejected for the
  same ecosystem reason; additionally, Angular 21's Signals model
  covers the same reactivity story within the chosen stack.
- **No framework / plain TypeScript + Web Components.** Rejected: the
  productivity delta vs. a full framework is too large for the scope
  of work Scout anticipates (provisioning wizard today, admin
  dashboard tomorrow).

## References

- [Angular homepage](https://angular.dev/)
- [Angular Signals guide](https://angular.dev/guide/signals)
- [Standalone components guide](https://angular.dev/guide/components)
- [Angular CLI](https://angular.dev/tools/cli)
- [Angular v21.2.9 release notes](https://github.com/angular/angular/releases/tag/21.2.9)
- [[pages/architecture/electron-provisioning-ui]] — renderer stack
  context, Bun + Angular toolchain notes, open questions.
- [[pages/decisions/0001-platform-priority|ADR-001 — Chat platform rollout order]]
  — companion ADR on Scout's runtime platform ordering.
