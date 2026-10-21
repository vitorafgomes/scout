---
title: "Scout Desktop - Visual Interface Design Document: 5. Screens: Screen 8 - Settings"
version: "1.0"
date: 2026-04-23
updated: 2026-10-21
changes: "Split out of 5-screens on 2026-10-21 (300-line rule)"
page_type: longform
status: active
description: "Screen specification: Screen 8 - Settings, part of the desktop UI design system."
source:
  - "obsidian-vault/scout/02-architecture/desktop-ui-design-system.md"
reliability: medium
---

# Scout Desktop - Visual Interface Design Document: 5. Screens: Screen 8 - Settings

**Purpose:** Application-level configuration, not Scout Core configuration (that lives in env vars passed to the worker).

**Sections:**
- Appearance (theme toggle, density)
- Logs (verbosity level, log file path, open log folder)
- Storage (userData path, keychain info, open folder)
- Provisioning (reset Discord provisioning, wipe stored tokens)
- About (version, licenses, changelog link)

```
+------------------+-----------------------------------------------+
| [Scout icon]     |  Settings                                     |
| Scout            |                                               |
|                  |                                               |
|------------------|  Appearance                                   |
| [#] Dashboard    |  Theme                                        |
| [*] Settings  <  |  [Dark]  [Light]  (segmented control)         |
|                  |                                               |
|                  |  Logs                                         |
|                  |  Log verbosity                                |
|                  |  [Debug] [Information] [Warning] [Error]      |
|                  |                                               |
|                  |  Log file location                            |
|                  |  /home/user/.config/Scout/logs/scout.log      |
|                  |  [Open log folder]                            |
|                  |                                               |
|                  |  Storage                                      |
|                  |  App data location                            |
|                  |  /home/user/.config/Scout/                    |
|                  |  [Open folder]                                |
|                  |                                               |
|                  |  Token storage: OS keychain (libsecret)       |
|                  |  [warn] No keyring detected — tokens stored   |
|                  |  with basic encryption. See docs.             |
|                  |                                               |
|                  |  Provisioning                                 |
|                  |  [ Reset Discord provisioning  ] (destructive)|
|                  |  Removes stored token and guild association.  |
|                  |  The bot will stop if currently running.      |
|                  |                                               |
|                  |  About                                        |
|                  |  Scout Desktop v0.1.0                         |
|                  |  Worker: Scout.Host.Worker v0.1.0             |
|                  |  [View changelog ->]   [View licenses ->]     |
+------------------+-----------------------------------------------+
```

Theme toggle is a segmented `sc-button` group (not `sc-switch`), since it is a three-way choice in the future (dark / light / system). Log verbosity is a segmented control pattern. "Reset Discord provisioning" is `sc-button` `destructive`. The keyring warning uses `sc-status-badge` `warn` inline.

---
