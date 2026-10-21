---
title: "Scout Desktop - Visual Interface Design Document: 9. Open Design Questions"
version: "1.0"
date: 2026-04-23
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: longform
status: active
description: "Open design questions left unresolved in the Scout Desktop visual design document."
source:
  - "obsidian-vault/scout/02-architecture/desktop-ui-design-system.md"
reliability: medium
---

# Scout Desktop - Visual Interface Design Document: 9. Open Design Questions

1. **Logo finalisation.** The crosshair-in-circle monogram concept needs sign-off. An alternative reading of "Scout" as surveillance/telescope tooling suggests a minimal telescope or scope reticle icon — both are worth prototyping before implementation locks the SVG.

2. **Product name casing and spacing.** Is the product "Scout", "Scout Desktop", or "Scout Desktop App" in the titlebar and first-run screen? Consistent naming matters early for the app's OS registration name, auto-updater manifest, and documentation.

3. **Telemetry opt-in screen.** Scout is MIT-licensed and local-first. If any telemetry (crash reports, usage pings) is planned, a dedicated consent screen before the Dashboard is mandatory — it cannot be buried in settings. If there is no telemetry, this question is permanently closed.

4. **Wizard placement: modal vs. dedicated route.** This design treats the wizard as a dedicated screen replacing the main content area (a route). An alternative is a multi-step modal overlay on top of the Dashboard. The route approach was chosen here because it allows the full window width without backdrop occlusion and maps cleanly to Angular Router. If the wizard becomes modal, the layout and step indicator design need revision.

5. **Tray icon behaviour on close.** When the user closes the window with a bot running: does the app quit (stopping the worker), minimise to tray (keeping the worker alive), or ask? GitHub Desktop quits; Tailscale stays in tray. Scout's answer affects the titlebar close button label and requires an OS tray icon design (a 16px version of the mono icon).

6. **Multi-platform worker concurrency model.** Once Slack ships (phase 2), will there be one worker process per platform or one worker with hot-loaded adapters? This directly affects the Bot Running screen — a single status screen works for one process; multiple platforms need either a per-platform sub-screen or a multi-row status table.

7. **Theme toggle control type.** Settings currently shows a three-way segmented control (Dark / Light / System). If "System" (follow OS) is not in scope for M1, collapse to `sc-switch` (Dark default, Light on toggle) and defer the three-way selector.

8. **Keychain warning severity.** On Linux without a desktop keyring, `safeStorage` falls back to near-plaintext. The current design shows a `warn` badge in Settings. Should this be elevated to a blocking `sc-dialog` on first run on affected systems, given the security implication for a credential-storing tool?

9. **Reset provisioning confirmation flow.** "Reset Discord provisioning" is a destructive action in Settings. The design specifies a `sc-button` `destructive` with descriptive copy below. Should this gate behind a confirmation `sc-dialog` with the bot's guild name typed in, or is a simple one-click confirm dialog sufficient? Given that re-provisioning is not catastrophic (the bot token can be re-entered), a simple dialog is likely proportionate.

10. **First-run vs. returning user detection.** The Welcome screen (Screen 1) should only appear once. After the wizard is completed, the app opens to the Dashboard (or Bot Running screen) on every subsequent launch. This requires a `hasCompletedOnboarding` flag in `localStorage` or Electron's `store`. Define the flag name and ownership (main process or renderer) before implementation begins.
