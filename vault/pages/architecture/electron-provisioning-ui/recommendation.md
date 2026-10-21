---
title: "Electron Provisioning UI - feasibility study: Recommendation"
version: "1.0"
date: 2026-04-22
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: longform
status: active
description: "Recommendation of the Electron provisioning feasibility study: a guided wizard per platform automating everything the platforms allow."
source:
  - "obsidian-vault/scout/02-architecture/electron-provisioning-ui.md"
reliability: medium
---

# Electron Provisioning UI - feasibility study: Recommendation

**Stack (locked): Angular + Bun + Electron.** No React/Vue/Svelte, no
npm/yarn/pnpm, no alternative desktop framework.

**Do not build this pre-V1.** Reasons in priority order:

1. ADR-001 scopes V1 to Discord only. An Electron cross-platform
   provisioner solves a V2+ problem.
2. The best-case UX for Discord is still a wizard-with-deep-links, not
   one-click. A `scout init` **CLI wizard** delivers 90% of the same
   UX with 5% of the code, and is more contributor-friendly for an
   OSS chatops bot. The wizard is implemented as a **Bun-native
   single-file executable** produced via `bun build --compile`. End
   users get one binary per OS/arch with no JS runtime to install.
3. Teams is blocked on policy the end user usually cannot change. A
   tool that works "for Discord + Slack, maybe Teams" will confuse
   more users than it helps, for V1.
4. Electron adds a large distribution surface (per-OS signing, auto
   update, Gatekeeper notarization on macOS, SmartScreen reputation
   on Windows) — all non-trivial work for an OSS project without a
   funded release engineer.

**Target UX per platform, if we *do* build it later:**

- **Discord** — "Sign in → paste token → we do the rest." Wizard opens
  Developer Portal, guides the user through app + bot creation, then
  drives OAuth2 guild install automatically. 6 clicks total.
- **Slack** — "Sign in → paste config token → done." After the one-time
  config token step, Scout creates the app via
  `apps.manifest.create`, drives workspace install, stores bot +
  app-level tokens, launches worker. 4 clicks total. **Closest to
  one-click of the three.**
- **Teams** — Not attempted. If pursued post-V2: require an
  Azure subscription, run MSAL admin consent, use Graph to publish
  and install the app, require Dev Tunnels for the webhook. Realistic
  audience: individual Microsoft 365 admins. Not a "download Scout
  and click Teams" experience.

**If we do pursue this, phase it:**

- Phase 1 (post-V1): Discord-only Electron wizard. Validates the
  pattern, no cross-platform complexity. Entire dev workflow runs on
  Bun: `bun install`, `bun run dev`, `bun test`, `bun run dist`.
- Phase 2: add Slack (fully automated, highest-value UX payoff).
- Phase 3: evaluate Teams only if a clear user demand exists.
