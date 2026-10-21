---
title: "Scout Desktop - Visual Interface Design Document: 5. Screens: Screen 3 - Discord Wizard - Step 1: Create the App"
version: "1.0"
date: 2026-04-23
updated: 2026-10-21
changes: "Split out of 5-screens on 2026-10-21 (300-line rule)"
page_type: longform
status: active
description: "Screen specification: Screen 3 - Discord Wizard - Step 1: Create the App, part of the desktop UI design system."
source:
  - "obsidian-vault/scout/02-architecture/desktop-ui-design-system.md"
reliability: medium
---

# Scout Desktop - Visual Interface Design Document: 5. Screens: Screen 3 - Discord Wizard - Step 1: Create the App

**Purpose:** Guide the user to create a Discord application in the Developer Portal, explain why Scout cannot do it for them.

**Primary action:** "I've created the app — continue"

**Secondary actions:** "Open Discord Developer Portal" (external link), "Back"

**Empty/loading/error:** If the user clicks "continue" without having actually created an app, validation on step 2 (token entry) catches it.

```
+------------------+-----------------------------------------------+
| [Scout icon]     |  Provision Discord                            |
| Scout            |  [1: Create app]--[2: Token]--[3: OAuth]--    |
|                  |  [4: Launch]                                  |
|------------------|                                               |
| [#] Dashboard    |  Step 1 of 4 — Create the Discord app         |
| [*] Settings     |                                               |
|                  |  Discord does not have a public API for        |
|                  |  creating applications. You'll need to do     |
|                  |  this one step in the Developer Portal.       |
|                  |                                               |
|                  |  What to do:                                  |
|                  |  1. Click the link below to open the          |
|                  |     Developer Portal.                         |
|                  |  2. Click "New Application".                  |
|                  |  3. Name it anything — "Scout" works fine.   |
|                  |  4. Click "Bot" in the left sidebar.          |
|                  |  5. Click "Add Bot".                          |
|                  |                                               |
|                  |  [Open Discord Developer Portal ->]           |
|                  |                                               |
|                  |  -------------------------------------------- |
|                  |                                               |
|                  |  [ Back ]          [ I've created the app  ] |
+------------------+-----------------------------------------------+
```

The "Open Discord Developer Portal" button is `sc-button` `secondary` with `[external]`. The "I've created the app" button is `primary`. The step indicator is `sc-step-indicator` with `activeStep = 0`.

---
