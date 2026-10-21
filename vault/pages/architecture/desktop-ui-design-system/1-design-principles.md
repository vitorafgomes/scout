---
title: "Scout Desktop - Visual Interface Design Document: 1. Design Principles"
version: "1.0"
date: 2026-04-23
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: longform
status: active
description: "Design principles for the Scout Desktop provisioning application interface."
source:
  - "obsidian-vault/scout/02-architecture/desktop-ui-design-system.md"
reliability: medium
---

# Scout Desktop - Visual Interface Design Document: 1. Design Principles

1. **The active step is never ambiguous.** Every screen surfaces one primary action in the first viewport; all secondary options live below the fold or in a secondary pane.
2. **Errors are diagnostic, not decorative.** Every error state names what failed, why it likely failed, and what the user should do next — no generic "something went wrong" copy.
3. **The app earns trust through transparency.** Long-running operations expose their progress, log output, and a cancel path at all times; nothing runs silently in the background.
4. **Platform constraints are stated honestly.** Where Discord or Teams require a manual human step, the UI explains the platform limitation directly rather than hiding it behind a loading state.
5. **Tokens are treated as secrets from the moment they are typed.** Token fields mask by default, do not log, do not echo into visible DOM attributes, and are never displayed in full after submission.
6. **The information density is professional, not padded.** Whitespace opens breathing room around content groups, not between individual data points; this is a tool for developers, not a landing page.
7. **State persists gracefully across restarts.** Completed wizard steps are remembered; re-opening the app after a crash shows the last known worker status rather than the welcome screen.

---
