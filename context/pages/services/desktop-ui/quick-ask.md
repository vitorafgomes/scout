---
title: "Desktop UI: Quick ask"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Desktop UI service page."
page_type: service
status: active
description: "Read when a change touches: Quick ask."
source: - ui/packages/main/src - ui/packages/preload/src - ui/packages/renderer/src - ui/packages/shared/src - ui/build
reliability: high
---

## Quick ask

The floating button, the quick-ask window, the shortcut, voice turns, the shared local conversation and the Chat screen (one question at a time, the composer, Stop, and the main window's one scroll with the post-connect shell's host fixed to the window, `ui/packages/renderer/src/app/shell/post-connect-shell.component.ts:132`) are described on [[pages/services/desktop-quick-ask]] ([[pages/decisions/0028-local-quick-ask-assistant]]), and so are reading replies aloud and the voice conversation ([[pages/decisions/0050-read-aloud-and-conversation-mode]]): `scout:local.speak` to the worker's voice, a speaker on each reply, "Read answers aloud", and the round accent button with the `sound-wave` glyph beside the dictation microphone, both drawn by us on the 24 grid in the `sc-icon` registry with `speaker` (`ui/packages/renderer/src/app/core/icon.component.ts:35`). `buildEnv` sets `SCOUT__SPEECH__DIRECTORY` to `<userData>/speech`, empty without the folder, and the supervisor strips an inherited value of it like the meetings path.
