---
title: "Scout Desktop - Visual Interface Design Document: 3. Layout System"
version: "1.0"
date: 2026-04-23
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: longform
status: active
description: "Layout system for Scout Desktop: frameless window with a custom titlebar, sidebar and content regions."
source:
  - "obsidian-vault/scout/02-architecture/desktop-ui-design-system.md"
reliability: medium
---

# Scout Desktop - Visual Interface Design Document: 3. Layout System

### 3.1 Window Dimensions

| Mode | Value | Justification |
|---|---|---|
| Minimum | 840 × 560px | Fits the 4-step wizard form at 80-column density without scroll |
| Default | 1024 × 680px | Comfortable on a 1280px laptop; shows rail + content + log pane |
| Maximum | User-resizable, no cap | Developer power users resize as needed |

### 3.2 Chrome Choice: Frameless with Custom Titlebar

Frameless (`frame: false` in Electron `BrowserWindow`) with a custom Angular-rendered titlebar region. Justification:

- Gives Scout a consistent cross-platform appearance (macOS titlebar buttons differ visually from Windows; a custom titlebar normalises this).
- Keeps the window decoration inside the Angular component tree, so the active screen title, status badge, and window controls are co-located and can react to app state via Signals.
- The drag region is set via `-webkit-app-region: drag` on the titlebar `div` and `no-drag` on all interactive controls within it.
- The custom titlebar is minimal: Scout wordmark left, screen title centre, window controls right (macOS: hidden, using native overlay via `titleBarStyle: 'hiddenInset'`; Windows/Linux: custom SVG min/max/close buttons).

### 3.3 Primary Layout: Three-Column Shell

```
+------------------+--------------------------------+--------------------+
| Left rail (200px)|  Main content (flex-grow)      | Right pane (280px) |
| Nav + brand      |  Active screen                 | Logs / detail      |
| always visible   |                                | contextual, toggle |
+------------------+--------------------------------+--------------------+
```

- The **left rail** is fixed at 200px. It holds the Scout wordmark, primary navigation, and at the bottom: settings icon and theme toggle.
- The **main content** area is `flex: 1`, scrollable independently.
- The **right pane** is a contextual panel — visible only on the Dashboard (log tail) and Bot Running screens. It can be toggled collapsed to 0px via a `[sc-split-pane]` component. In the wizard, the right pane is hidden entirely.
- The right pane opens from the right edge without pushing the main content — it overlaps on narrow windows, slides in on wide.

### 3.4 Window Breakpoints

These are functional breakpoints for window resize, not CSS media queries in the traditional sense.

| Name | Min-width | Behaviour |
|---|---|---|
| `compact` | 840px | Right pane collapses to icon-only toggle; left rail shows icons only (no labels) |
| `default` | 960px | Left rail shows icon + label; right pane available but closed by default |
| `expanded` | 1140px | Right pane opens by default when content warrants it |

Implemented via a Signal that reads `window.innerWidth` (updated on `resize` events with a 100ms debounce) and drives `@if` conditions in the shell component.

---
