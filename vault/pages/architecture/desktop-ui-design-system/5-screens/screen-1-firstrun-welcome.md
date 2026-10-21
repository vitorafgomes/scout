---
title: "Scout Desktop - Visual Interface Design Document: 5. Screens: Screen 1 - First-run / Welcome"
version: "1.0"
date: 2026-04-23
updated: 2026-10-21
changes: "Split out of 5-screens on 2026-10-21 (300-line rule)"
page_type: longform
status: active
description: "Screen specification: Screen 1 - First-run / Welcome, part of the desktop UI design system."
source:
  - "obsidian-vault/scout/02-architecture/desktop-ui-design-system.md"
reliability: medium
---

# Scout Desktop - Visual Interface Design Document: 5. Screens: Screen 1 - First-run / Welcome

**Purpose:** Orient the user. Explain what Scout is in two sentences. Gate on a single action.

**Primary action:** "Get started"

**Secondary actions:** "View documentation" (external link), "View on GitHub" (external link)

**Empty/loading/error:** Not applicable — this screen has no data dependencies.

```
+------------------------------------------------------------------------+
|  [Custom titlebar — frameless]   Scout              [_ ][  ][X]        |
+------------------------------------------------------------------------+
|                                                                        |
|                                                                        |
|                    [Scout icon — 48px]                                 |
|                                                                        |
|                    Scout                                               |
|                    A read-only chatops assistant for engineering        |
|                    teams. Run it on your Discord server in             |
|                    under 10 minutes.                                   |
|                                                                        |
|                    [ Get started                    ]   <- primary     |
|                                                                        |
|                    View documentation [->]   GitHub [->]               |
|                                                                        |
|                    MIT License — v0.1.0                                |
|                                                                        |
|                                                                        |
+------------------------------------------------------------------------+
```

No navigation rail on the welcome screen — the rail appears after "Get started" is clicked and the app transitions to the Dashboard. The welcome screen is full-window centred content.

---
