---
title: "Scout Desktop - Visual Interface Design Document: 5. Screens: Screen 2 - Dashboard / Home"
version: "1.0"
date: 2026-04-23
updated: 2026-10-21
changes: "Split out of 5-screens on 2026-10-21 (300-line rule)"
page_type: longform
status: active
description: "Screen specification: Screen 2 - Dashboard / Home, part of the desktop UI design system."
source:
  - "obsidian-vault/scout/02-architecture/desktop-ui-design-system.md"
reliability: medium
---

# Scout Desktop - Visual Interface Design Document: 5. Screens: Screen 2 - Dashboard / Home

**Purpose:** Show the user which platforms are available to provision. Once a bot is running, show live status.

**Primary action:** "Provision Discord" (M1)

**Secondary actions:** Docs link, view log (right pane), settings icon

**States:**
- **Pre-provisioning (empty):** Three platform cards, Discord active, Slack/Teams greyed. No activity feed — `sc-empty-state` in main content area beneath the cards.
- **Provisioning in progress:** Discord card shows `provisioning` badge, CTA disabled, breadcrumb shows wizard step.
- **Bot running:** Discord card shows `connected` badge, CTA changes to "View status". Activity feed / recent commands section appears below cards. Right pane shows `sc-log-viewer` tail.
- **Error:** Discord card shows `error` badge, CTA changes to "View error" which opens a `sc-dialog` with error detail.

```
+------------------+-----------------------------------------------+----------+
| [Scout icon]     |  Dashboard                    [settings icon] | Logs  [X]|
| Scout            |                                               |          |
|------------------|  Platform connections                         | INFO  ... |
| [#] Dashboard  < |                                               | INFO  ... |
| [*] Settings     |  +---------------+ +----------+ +----------+ | WARN  ... |
|                  |  | Discord       | | Slack    | | Teams    | | INFO  ... |
|                  |  | [icon]        | | [icon]   | | [icon]   | |          |
|                  |  | [Detected]    | |[Detected]| |[ - ]     | | [pause]  |
|                  |  | [Connected]   | |[Coming   | |[Coming   | | [copy]   |
|                  |  |               | | soon]    | | soon]    | | [clear]  |
|                  |  | [View status] | |[--]      | |[--]      | |          |
|                  |  +---------------+ +----------+ +----------+ |          |
|                  |                                               |          |
|                  |  Recent activity                              |          |
|                  |  No activity yet.                             |          |
|                  |  Provision a platform to see commands here.  |          |
|                  |                                               |          |
+------------------+-----------------------------------------------+----------+
```

When no bot is running, the right pane is hidden. The layout collapses to two-column (rail + main).

---
