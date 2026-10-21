---
title: "Scout Desktop - Visual Interface Design Document: 5. Screens: Screen 5 - Discord Wizard - Step 3: OAuth2 Install"
version: "1.0"
date: 2026-04-23
updated: 2026-10-21
changes: "Split out of 5-screens on 2026-10-21 (300-line rule)"
page_type: longform
status: active
description: "Screen specification: Screen 5 - Discord Wizard - Step 3: OAuth2 Install, part of the desktop UI design system."
source:
  - "obsidian-vault/scout/02-architecture/desktop-ui-design-system.md"
reliability: medium
---

# Scout Desktop - Visual Interface Design Document: 5. Screens: Screen 5 - Discord Wizard - Step 3: OAuth2 Install

**Purpose:** Scout drives the OAuth2 guild install flow. The user authorises in the browser; Scout listens on a local callback port.

**Primary action:** None directly — the action is in the external browser. "Open authorisation URL" is the trigger.

**Secondary actions:** "Cancel OAuth flow", "Back", "Retry"

**States:**
- **Waiting:** Spinner + "Waiting for authorisation in your browser…" with the port number shown. Cancel button always visible.
- **Success:** Spinner replaced by a green checkmark. Guild name + icon displayed. "Continue" button appears.
- **Cancelled:** User closed the browser tab. Error state: "Authorisation was cancelled. Try again or go back to verify your token."
- **Timeout (90 seconds):** Error state: "No response received. The local callback server timed out. Try again."
- **Port conflict:** Error state: "Port 53682 is in use by another application. Quit that application and retry."

```
+------------------+-----------------------------------------------+
| [Scout icon]     |  Provision Discord                            |
| Scout            |  [1: Create]-[2: Token]-[3: OAuth *]--        |
|                  |  [4: Launch]                                  |
|------------------|                                               |
| [#] Dashboard    |  Step 3 of 4 — Install to your server         |
| [*] Settings     |                                               |
|                  |  Scout will open Discord in your browser.     |
|                  |  Choose a server and click "Authorise".       |
|                  |                                               |
|                  |  [ Open authorisation URL ->  ]               |
|                  |                                               |
|                  |  +-----------------------------------------+ |
|                  |  |  [spinner]                              | |
|                  |  |  Waiting for authorisation...           | |
|                  |  |  Listening on localhost:53682           | |
|                  |  |  Elapsed: 0:23    [Cancel]              | |
|                  |  +-----------------------------------------+ |
|                  |                                               |
|                  |  (Once authorised, your server name appears   |
|                  |   here and "Continue" becomes available.)     |
|                  |                                               |
|                  |  [ Back ]                                     |
+------------------+-----------------------------------------------+
```

The waiting panel is `sc-card` variant `inset`. The elapsed counter ticks every second via a `Signal<number>`. The spinner is a simple CSS border-spin (no library dependency). Cancel button is `sc-button` `ghost` inside the panel.

---
