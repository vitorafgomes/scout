---
title: "Desktop UI: Desktop presence"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Desktop UI service page."
page_type: service
status: active
description: "Read when a change touches: Desktop presence."
source: - ui/packages/main/src - ui/packages/preload/src - ui/packages/renderer/src - ui/packages/shared/src - ui/build
reliability: high
---

## Desktop presence

**Application menu.** Before any window opens, main replaces Electron's default File, Edit, View and Window menu (`installAppMenu`, `ui/packages/main/src/shell/app-menu.ts:31`, called at `ui/packages/main/src/main.ts:390`): on Linux and Windows there is no menu, and every window main creates hides the menu bar (`autoHideMenuBar`, `ui/packages/main/src/browser-window-factory.ts:25`, `withoutMenuBar` at `ui/packages/main/src/shell/app-menu.ts:36`), so no bar appears even if a menu is set later; copy, cut, paste, undo and select all keep working in text fields because Chromium handles those keys without a menu. On macOS a minimal menu stays, the app menu, Edit (undo, redo, cut, copy, paste, select all) and Window (`:9`), because the copy and paste shortcuts there come from the Edit roles. The menu's other shortcuts are gone on Linux and Windows: reload, force reload, developer tools, zoom in, zoom out, actual size and full screen. Tests: `ui/packages/main/test/shell-app-menu.test.ts`, `ui/packages/main/test/browser-window-factory.test.ts`.

Settings has a Desktop section with "Keep Scout running when the window is closed" (on by default) and "Start Scout when I log in" (off by default), served by `scout:desktop.getSettings`, `setKeepRunning` and `setStartAtLogin` (`ui/packages/main/src/ipc/desktop.ts:60`) ([[pages/decisions/0027-desktop-presence]]). `main.ts` creates the tray after the supervisor, and the supervisor after connecting the control plane (`ui/packages/main/src/main.ts:876`, `:811`): Open Scout, Ask Scout..., Start Scout or Stop Scout, Quit Scout, tooltip `Scout - <state>`, plus "Update available: <version>" and a tooltip suffix while a newer release is known (see Update notice). `installAppLifecycle` (`ui/packages/main/src/shell/app-lifecycle.ts:81`) owns `window-all-closed`, `before-quit`, `second-instance` and `activate`: with keep running on, closing the window hides it and the worker keeps running; Quit Scout goes through `app.quit()` and the same `before-quit` that stops the worker. If the tray cannot be created, keep running is off for the session and Settings says "This desktop has no system tray, so Scout quits when its window is closed." Start at login uses the OS login item on macOS and Windows and `~/.config/autostart/scout.desktop` on Linux (`ui/packages/main/src/shell/login-item.ts:44`); a login launch carries `--hidden`, which loads the window without showing it, so the welcome screen's worker auto-start still runs. Ask Scout... calls the quick-ask module's `openQuickAsk` (`ui/packages/main/src/main.ts:887`), which creates and shows the quick-ask window (see Quick ask). The hidden quick-ask window never keeps Scout alive: with keep running off, closing the main window quits through `before-quit` even while that window exists, except on macOS (`ui/packages/main/src/shell/app-lifecycle.ts:172`), and a second launch or `activate` shows the main window, never the quick-ask one, unless the launch carries `--ask` (see Quick ask). Native notifications (`ui/packages/main/src/shell/notifications.ts:24`): the supervisor gave up on the worker (status `crashed` with `gaveUp`, `ui/packages/main/src/worker/supervisor.ts:162`), as "could not restart" or "could not start"; a pairing completed; the first hide to the tray; and, on Linux, every `--hidden` start, because a tray icon may be invisible there. A Linux desktop without notifications never starts hidden (`ui/packages/main/src/shell/app-lifecycle.ts:69`).

### Update notice

The check, the install, the progress channel, the bar, the tray and the settings are on [[pages/services/desktop-channels]] (Update notice); [[pages/decisions/0031-desktop-self-update]].
