---
title: "Scout Desktop - Visual Interface Design Document: 5. Screens: Screen 9 - Error States"
version: "1.0"
date: 2026-04-23
updated: 2026-10-21
changes: "Split out of 5-screens on 2026-10-21 (300-line rule)"
page_type: longform
status: active
description: "Screen specification: Screen 9 - Error States, part of the desktop UI design system."
source:
  - "obsidian-vault/scout/02-architecture/desktop-ui-design-system.md"
reliability: medium
---

# Scout Desktop - Visual Interface Design Document: 5. Screens: Screen 9 - Error States

Four distinct error screens. Each uses the same layout: the main content area shows an `sc-empty-state` variant with a diagnostic block.

**9a — No Discord client detected**

```
+------------------+-----------------------------------------------+
|                  |  Dashboard                                    |
|                  |                                               |
|                  |      [alert-circle icon, 40px]                |
|                  |      Discord not detected on this machine     |
|                  |      Scout checks for the Discord desktop     |
|                  |      client. It was not found. You can still  |
|                  |      provision a bot — detection is optional. |
|                  |                                               |
|                  |      [ Provision anyway ]  [ Learn more -> ]  |
+------------------+-----------------------------------------------+
```

Detection failure is non-blocking. The "Provision anyway" button starts the wizard.

**9b — OAuth2 cancelled by user**

```
+------------------+-----------------------------------------------+
|                  |  Provision Discord — Step 3 of 4              |
|                  |                                               |
|                  |      [x-circle icon, 40px]                    |
|                  |      Authorisation cancelled                  |
|                  |      You closed the browser tab before        |
|                  |      authorising Scout. Your bot was not      |
|                  |      added to any server.                     |
|                  |                                               |
|                  |      [ Try again ]   [ Back to step 2 ]       |
+------------------+-----------------------------------------------+
```

**9c — Worker binary missing**

```
+------------------+-----------------------------------------------+
|                  |  Provision Discord — Step 4 of 4              |
|                  |                                               |
|                  |      [file-x icon, 40px]                      |
|                  |      Worker binary not found                  |
|                  |      Scout could not find Scout.Host.Worker   |
|                  |      at the expected path:                    |
|                  |                                               |
|                  |  +---------------------------------------+    |
|                  |  | /Applications/Scout.app/Contents/    |    |
|                  |  |   Resources/scout-worker              |    |
|                  |  +---------------------------------------+    |
|                  |                                               |
|                  |      This usually means the app was not      |
|                  |      installed correctly. Re-install Scout.   |
|                  |                                               |
|                  |      [ Download installer -> ]               |
+------------------+-----------------------------------------------+
```

The path is shown in `sc-code-block`.

**9d — Token rejected by Discord**

```
+------------------+-----------------------------------------------+
|                  |  Provision Discord — Step 4 of 4              |
|                  |                                               |
|                  |      [shield-x icon, 40px]                    |
|                  |      Token rejected by Discord (401)          |
|                  |      The bot token you entered is invalid or  |
|                  |      has been revoked. Discord returned a     |
|                  |      401 Unauthorized response.               |
|                  |                                               |
|                  |      To fix this: go to the Discord Developer |
|                  |      Portal, reset your bot token, and        |
|                  |      re-enter it in step 2.                   |
|                  |                                               |
|                  |      [ Back to step 2 ]   [ Open Portal -> ] |
+------------------+-----------------------------------------------+
```

---
