---
title: "Scout Desktop - Visual Interface Design Document: 5. Screens: Screen 7 - Bot Running"
version: "1.0"
date: 2026-04-23
updated: 2026-10-21
changes: "Split out of 5-screens on 2026-10-21 (300-line rule)"
page_type: longform
status: active
description: "Screen specification: Screen 7 - Bot Running, part of the desktop UI design system."
source:
  - "obsidian-vault/scout/02-architecture/desktop-ui-design-system.md"
reliability: medium
---

# Scout Desktop - Visual Interface Design Document: 5. Screens: Screen 7 - Bot Running

**Purpose:** Operational view while the Scout worker is active. Primary monitoring screen.

**Primary actions:** "Restart", "Stop"

**Secondary actions:** Open settings, view full log, copy process ID

**States:**
- **Connected:** Green `connected` badge, uptime counter, log streaming.
- **Disconnected (unexpected):** Badge changes to `error`. Stop button hidden, "Restart" becomes primary. Log shows last lines before disconnect.
- **Stopped (user-initiated):** Badge changes to `idle`. Both action buttons show "Start" only.

```
+------------------+-----------------------------------------------+----------+
| [Scout icon]     |  Bot running                   [settings]    | Logs  [X]|
| Scout            |                                               |          |
|                  |                                               | INFO  ... |
|------------------|  Discord — My Dev Server                      | INFO  ... |
| [#] Dashboard  < |  [Connected]   Uptime: 2h 14m 03s            | WARN  ... |
| [*] Settings     |                                               | INFO  ... |
|                  |  +-----------------------------------+        |          |
|                  |  | Worker PID: 48291      [copy]     |        | INFO  ... |
|                  |  | Started: 14:23 BST                |        |          |
|                  |  | Log level: Information            |        |[jump-bot]|
|                  |  +-----------------------------------+        |          |
|                  |                                               | [pause]  |
|                  |  Recent commands                              | [copy]   |
|                  |  14:28  @scout is prod Azure healthy?         | [clear]  |
|                  |  14:15  @scout summarise the checkout flow    |          |
|                  |  13:50  @scout aws us-east-1 status           |          |
|                  |                                               |          |
|                  |  [ Stop  ]  [ Restart ]                       |          |
+------------------+-----------------------------------------------+----------+
```

The right pane is `sc-log-viewer`. "Stop" is `sc-button` `destructive`. "Restart" is `sc-button` `secondary`. The status badge is `sc-status-badge`. The uptime counter ticks every second via Signal.

---
