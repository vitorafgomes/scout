---
title: "Scout Desktop - Visual Interface Design Document: 5. Screens: Screen 6 - Discord Wizard - Step 4: Launch Worker"
version: "1.0"
date: 2026-04-23
updated: 2026-10-21
changes: "Split out of 5-screens on 2026-10-21 (300-line rule)"
page_type: longform
status: active
description: "Screen specification: Screen 6 - Discord Wizard - Step 4: Launch Worker, part of the desktop UI design system."
source:
  - "obsidian-vault/scout/02-architecture/desktop-ui-design-system.md"
reliability: medium
---

# Scout Desktop - Visual Interface Design Document: 5. Screens: Screen 6 - Discord Wizard - Step 4: Launch Worker

**Purpose:** Show the user the env vars Scout will pass to the worker, then launch it and stream stdout.

**Primary action:** "Launch Scout"

**Secondary actions:** "Back", "Cancel launch"

**States:**
- **Pre-launch:** Env var summary in a `sc-code-block`. "Launch Scout" button primary.
- **Launching:** Button becomes loading state. Log viewer appears below, streaming stdout.
- **Success:** Log shows gateway connection line. Button changes to "View live status" (navigates to Bot Running screen). Step indicator marks step 4 complete.
- **Binary missing:** Error state (see Screen 9).
- **Token rejected:** Error state — Discord gateway returns 401. See Screen 9.

```
+------------------+-----------------------------------------------+
| [Scout icon]     |  Provision Discord                            |
| Scout            |  [1]-[2]-[3]-[4: Launch *]                    |
|                  |                                               |
|------------------|  Step 4 of 4 — Launch Scout                   |
| [#] Dashboard    |                                               |
| [*] Settings     |  Scout will start the worker with these       |
|                  |  environment variables:                       |
|                  |                                               |
|                  |  +-----------------------------------------+ |
|                  |  | dotenv                      [Copy all]  | |
|                  |  | DISCORD_BOT_TOKEN=••••••••••••••••      | |
|                  |  | DISCORD_GUILD_ID=1234567890123456       | |
|                  |  | LLM_PROVIDER=Ollama                     | |
|                  |  | OLLAMA_ENDPOINT=http://localhost:11434  | |
|                  |  +-----------------------------------------+ |
|                  |                                               |
|                  |  [ Back ]              [ Launch Scout      ] |
|                  |                                               |
|                  |  (Log output appears here after launch)       |
+------------------+-----------------------------------------------+
```

Token value is masked with bullet characters in the display. The env var block is `sc-code-block` with `language="dotenv"`. The token value shown is the masked version — copying via "Copy all" copies the real value.

---
