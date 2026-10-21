---
title: "Scout Desktop - Visual Interface Design Document: 5. Screens: Screen 4 - Discord Wizard - Step 2: Token + Intents"
version: "1.0"
date: 2026-04-23
updated: 2026-10-21
changes: "Split out of 5-screens on 2026-10-21 (300-line rule)"
page_type: longform
status: active
description: "Screen specification: Screen 4 - Discord Wizard - Step 2: Token + Intents, part of the desktop UI design system."
source:
  - "obsidian-vault/scout/02-architecture/desktop-ui-design-system.md"
reliability: medium
---

# Scout Desktop - Visual Interface Design Document: 5. Screens: Screen 4 - Discord Wizard - Step 2: Token + Intents

**Purpose:** Collect the bot token, verify its format, and confirm the three required intents are enabled.

**Primary action:** "Continue"

**Secondary actions:** "Back", help link for intents

**States:**
- **Default:** Token field empty, intents unchecked.
- **Paste detected:** Pre-filled with a confirmation banner: "Token pasted from clipboard — verify it looks correct before continuing."
- **Token format error:** Inline error under the field.
- **Token verified (format):** Green checkmark icon appears inline. Note: actual validation happens server-side when the worker starts.
- **Intent warning:** If intents are not all checked, a `warn` inline message below the checkboxes: "Scout requires all three intents to function correctly."

```
+------------------+-----------------------------------------------+
| [Scout icon]     |  Provision Discord                            |
| Scout            |  [1: Create]-[2: Token *]--[3: OAuth]--       |
|                  |  [4: Launch]                                  |
|------------------|                                               |
| [#] Dashboard    |  Step 2 of 4 — Bot token and intents          |
| [*] Settings     |                                               |
|                  |  Bot token                                    |
|                  |  +------------------------------------------+|
|                  |  | ••••••••••••••••••••    [show] [paste]   ||
|                  |  +------------------------------------------+|
|                  |  Found in Discord Developer Portal            |
|                  |  Bot tab > Token > Reset Token                |
|                  |                                               |
|                  |  Required intents (enable in Portal > Bot)    |
|                  |  [x] Message Content Intent                   |
|                  |      Allows Scout to read message text.       |
|                  |  [x] Server Members Intent                    |
|                  |      Allows Scout to see guild members.       |
|                  |  [x] Presence Intent                          |
|                  |      Allows Scout to see online status.       |
|                  |                                               |
|                  |  [ Back ]                     [ Continue   ] |
+------------------+-----------------------------------------------+
```

Token field is `sc-input` type `token-with-reveal` wrapped in `sc-field-group`. Checkboxes are `sc-checkbox`. The help link "Found in Discord Developer Portal" is `text-sm text-muted` with an external link icon.

---
