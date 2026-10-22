---
title: "Electron Provisioning UI - feasibility study: Open questions"
version: "1.1"
date: 2026-04-22
updated: 2026-10-21
changes: "Angular version mentions moved from 21 to 22"
page_type: longform
status: active
description: "Open questions remaining from the Electron provisioning feasibility study."
source:
  - "obsidian-vault/scout/02-architecture/electron-provisioning-ui.md"
reliability: medium
---

# Electron Provisioning UI - feasibility study: Open questions

- Do we want Scout Desktop to *ship* the .NET worker, or to manage an
  already-installed Scout install? Trade-off: bundle size vs. update
  cadence. Default answer: ship self-contained.
- How does Scout Desktop handle multi-platform concurrency? Is it one
  worker process per platform, or one worker with multiple adapters
  loaded? The current `Scout.Core` assumes the latter; Electron
  detection + provisioning produces one credential per platform so the
  worker would need to hot-reload adapters. `TODO: decide` — probably
  a new ADR.
- OAuth callback port collision: multiple local callback servers on
  well-known ports will clash. Use an ephemeral port + store the
  actual port in the redirect URI? But Discord and Slack require
  redirect URIs to be pre-registered in the app config, which means
  the port cannot be random. Fix: register `http://localhost:53682`
  (a deliberately obscure port) in the manifest.
- Does Scout publish a public, claimable "Scout for Slack" app that
  every user installs, or does each user create their own app? The
  first is lower friction but requires Scout to run a distribution
  site and pass Slack's app review. Related to the read-only
  invariant (Scout handles no user data server-side, so distribution
  review should pass, but `TODO: verify`).
- For Teams, is there a realistic "Scout for Teams" marketplace
  listing that bypasses sideloading? That would require Microsoft's
  app store submission, which is months of calendar time. Punt.
- Should Scout Desktop be a separate repo from Scout core? Separate
  release cadence, separate license considerations — probably yes.
- Does `bun install` + `electron-builder` correctly trigger
  `@electron/rebuild` for native modules, or do we need a postinstall
  shim (`bun run electron-rebuild` wired into `scripts.postinstall`)?
  `TODO: verify` against current Bun + electron-builder releases and
  any tracking issue on the `oven-sh/bun` repo.
- Does `bun test` cover renderer DOM tests out of the box, or do we
  still need `jsdom` / Playwright for anything that touches the
  Electron renderer? Bun has a happy-dom integration path; verify
  whether that suffices for our renderer unit scope or whether
  Playwright (already useful for end-to-end flows) is the better
  default.
- `bun build --compile` cross-platform targets for the `scout init`
  CLI: what is the current supported triple matrix? We need at
  minimum `bun-windows-x64`, `bun-darwin-arm64`, `bun-darwin-x64`,
  `bun-linux-x64`, `bun-linux-arm64`. `TODO: verify` against Bun's
  executables docs.
- **Angular CLI + Bun interop.** Does `ng build` / `ng test` / `ng
  generate` work cleanly against a `bun install`-populated
  `node_modules`? Some Angular schematics and builders historically
  assume npm semantics (peer-dep resolution, lifecycle scripts, ng
  add's `npm install` shell-out). `TODO: verify` — if there is
  friction, either add a `package-lock.json` shim or document the
  npm-only escape hatch.
- **Renderer unit test runner.** Angular ships with Karma + Jasmine by
  default (deprecated path), with Jest and Vitest as the current
  community directions. `bun test` is a fourth option but has no
  first-class Angular support. `TODO: decide` — pick based on current
  Angular 22.x + Bun ecosystem maturity. Default lean: Vitest, since
  it aligns with Angular's esbuild-era tooling and plays better with
  Bun than Karma.
- **Angular SSR / hydration.** Angular 22 ships full SSR + hydration
  for server-rendered apps. For an Electron renderer this is almost
  certainly **not** relevant (offline desktop, no SEO, no cold HTML
  payload concern). `TODO: verify` the decision and document the
  opt-out so future contributors do not accidentally wire SSR into a
  desktop build.
