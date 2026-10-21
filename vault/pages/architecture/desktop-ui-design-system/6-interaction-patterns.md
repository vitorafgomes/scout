---
title: "Scout Desktop - Visual Interface Design Document: 6. Interaction Patterns"
version: "1.0"
date: 2026-04-23
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: longform
status: active
description: "Interaction patterns for Scout Desktop: keyboard shortcuts, clipboard detection, feedback and transitions."
source:
  - "obsidian-vault/scout/02-architecture/desktop-ui-design-system.md"
reliability: medium
---

# Scout Desktop - Visual Interface Design Document: 6. Interaction Patterns

### 6.1 Keyboard Shortcuts

| Shortcut | Action |
|---|---|
| `Cmd/Ctrl + ,` | Open Settings |
| `Cmd/Ctrl + L` | Focus log viewer / toggle right pane |
| `Cmd/Ctrl + K` | (Reserved — command palette, future) |
| `Esc` | Close modal / dialog; deactivate log pause |
| `Cmd/Ctrl + C` (in log viewer) | Copy selected log lines |
| `Tab / Shift+Tab` | Navigate interactive elements |
| `Enter / Space` | Activate focused button or toggle |
| `Arrow keys` | Navigate platform cards on dashboard |

Keyboard shortcuts are rendered in tooltips as `Ctrl+,` on Linux/Windows and `Cmd+,` on macOS, detected from `navigator.platform` (or, in Electron, from `process.platform`).

### 6.2 Paste Detection

When the user copies a string matching the Discord bot token pattern (`^[A-Za-z0-9_-]{24,28}\.[A-Za-z0-9_-]{6}\.[A-Za-z0-9_-]{27,38}$`) to the system clipboard, and then focuses the token input field, a confirmation banner appears above the field:

```
+------------------------------------------+
| Token detected in clipboard.             |
| [ Paste and continue ]   [ Dismiss ]     |
+------------------------------------------+
```

This uses a `clipboard-change` listener registered via Electron's `clipboard` module in the main process, forwarded via IPC. The Angular renderer subscribes to a Signal updated by the IPC event. The banner is not shown if the field already has a value. "Paste and continue" fills the field and moves focus to the first unchecked intent checkbox.

### 6.3 Copy-to-Clipboard Feedback

Every copy action (in `sc-code-block`, log viewer, env var blocks) follows this flow:
1. User clicks the copy button.
2. `navigator.clipboard.writeText()` called.
3. On success: button label changes from "Copy" to "Copied" for 2000ms, then reverts. No toast — the inline feedback is sufficient.
4. On failure (permissions denied): `sc-toast` `warn` fires with message "Copy failed — clipboard access denied. Copy manually."

### 6.4 External Links

Any link opening a browser (Discord Developer Portal, GitHub, documentation) uses `shell.openExternal()` from Electron. All such buttons or anchors display a 12px `arrow-up-right` SVG icon after the label. The icon has `aria-hidden="true"` and the label itself conveys the external destination ("Open Discord Developer Portal").

Never open external URLs inside the Electron renderer's BrowserWindow — this would give the external page access to Node integration context if not properly sandboxed.

### 6.5 Long-running Operations

All operations with indeterminate duration (OAuth callback listening, worker launch, worker health check) follow this pattern:
- A spinner (20px, CSS border-spin) appears in the action area.
- An elapsed timer shows `Elapsed: M:SS` updating every second via Signal.
- A "Cancel" button is always visible and functional. "Cancel" sends the appropriate termination signal (kills the local callback server, terminates the child process).
- An estimated time hint is shown where it can be calculated (OAuth: "Usually takes 10–30 seconds"; worker startup: "Usually takes 5–15 seconds").
- If the operation exceeds 2× the stated estimate, the hint changes to "Taking longer than expected…" without alarming the user.
- No indefinite spinner without a timeout. All operations have a maximum timeout after which the error state fires automatically.

---
