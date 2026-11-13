---
paths:
  - "ui/packages/main/src/**"
  - "ui/packages/preload/src/**"
  - "ui/packages/renderer/src/**"
  - "ui/packages/shared/src/**"
  - "ui/build/**"
---

# Desktop channels

## What to read for a change

Find the row your change matches and read that page. Read one or two rows, not the table.

| If the change touches | Read |
|---|---|
| IPC channels | `context/pages/services/desktop-channels/ipc-channels.md` |
| Whisper models | `context/pages/services/desktop-channels/whisper-models.md` |
| Update notice | `context/pages/services/desktop-channels/update-notice.md` |

For where a type lives or who calls it, ask the code graph (`graphify explain "<Type>"`, `graphify affected "<Type>"`).
