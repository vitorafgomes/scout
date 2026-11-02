---
title: "ADR 0027 - Scout stays resident on the desktop: tray, keep running, start at login, notifications"
version: "1.1"
date: 2026-10-30
updated: 2026-11-02
changes: "Amended by ADR 0031: the startup entry is refreshed at start when it exists"
page_type: adr
status: accepted
description: "Closing the window hides Scout to the system tray and keeps the worker running; the owner can start Scout at login with --hidden; three native notifications; without a tray Scout keeps the old quit-on-close behaviour."
supersedes: ""
superseded_by: ""
source:
  - chat
  - ui/packages/main/src/shell/
  - ui/packages/main/src/tray/tray.ts
  - ui/packages/main/src/ipc/desktop.ts
  - ui/packages/main/src/main.ts
  - ui/packages/renderer/src/app/wizard/settings/settings.component.ts
reliability: high
---

# ADR 0027 - Scout stays resident on the desktop: tray, keep running, start at login, notifications

> Closing the Scout window hides it to the system tray and the worker keeps answering in chat; the owner can make Scout start at login with no window; a desktop with no tray keeps the old quit-on-close behaviour, so Scout is never running invisibly.

## Status

- [ ] Proposed
- [x] Accepted
- [ ] Deprecated / superseded

Updated: 2026-10-30.

## Context

The owner asked whether Scout can "stay directly in the operating system" and chose the first level: stay resident. Until now `window-all-closed` quit the app, and with it the worker, everywhere but macOS, so closing the window stopped the bot. A tray controller from an earlier change (PR 45) sat in `ui/packages/main/src/tray/tray.ts` unwired, and the packaged app no longer shipped its icon. A second agent is building a quick-ask window with a global shortcut in parallel; this change leaves it a hook and builds none of it.

## Decision

- **Tray.** `createTrayController` (`ui/packages/main/src/tray/tray.ts:33`) shows the app icon (`ui/build/icons/icon.png`, shipped to `resources/icons` and found by `resolveTrayIcon`, `ui/packages/main/src/worker/resolve-binary.ts:81`; resized to 16 px on macOS, no template image yet), the tooltip `Scout - <worker state>` (`:75`) and the menu Open Scout, Ask Scout..., Start Scout or Stop Scout, Quit Scout. The worker item follows the supervisor's state events and is disabled while a start or a stop is in flight. Clicking the icon opens the window. Ask Scout... calls `openQuickAsk` from the quick-ask module (`ui/packages/main/src/quick-ask/quick-ask.ts:97`), a no-op until `registerQuickAsk` (`:220`) has run ([[pages/decisions/0028-local-quick-ask-assistant]]; when this ADR was written the tray called a stub that the quick-ask change replaced). Quit Scout calls `app.quit()`, so it takes the one `before-quit` path that stops the worker (`ui/packages/main/src/shell/app-lifecycle.ts:140`). Quit always exits: `stop()` cancels a scheduled restart and resolves at once when no worker process is alive, as after a crash or during the restart backoff (`ui/packages/main/src/worker/supervisor.ts:327`), and a restart already preparing its environment spawns nothing once a stop is requested.
- **Keep running.** Settings, Desktop, "Keep Scout running when the window is closed", on by default, stored under the main-only vault key `app.keep-running` (`ui/packages/main/src/ipc/desktop.ts:13`). While it is on, closing the window hides it (`ui/packages/main/src/shell/app-lifecycle.ts:167`) and `window-all-closed` does not quit (`:85`); while it is off, the behaviour is the old one. A second launch, the tray's Open Scout and the macOS dock show the existing window, or create it when it is gone (`:110`, `:114`); the second process quits on the single-instance lock.
- **No tray, no hiding.** When the tray cannot be created, `createTrayController` returns null (`ui/packages/main/src/tray/tray.ts:43`); keep running is then off for the session whatever is stored (`ui/packages/main/src/ipc/desktop.ts:38`), the toggle is locked, Settings says "This desktop has no system tray, so Scout quits when its window is closed.", and a `--hidden` launch shows the window (`ui/packages/main/src/shell/app-lifecycle.ts:66`). On Linux a `--hidden` launch also needs notifications: where they are unsupported the window is shown, and where they are supported the hidden start announces itself (see Notifications).
- **Start at login.** Settings, Desktop, "Start Scout when I log in", off by default; its state is read from the OS, never stored. macOS and Windows call `app.setLoginItemSettings({ openAtLogin, args: ['--hidden'] })` (`ui/packages/main/src/shell/login-item.ts:79`). Linux writes `<home>/.config/autostart/scout.desktop` through a temp file and a rename, mode 0644 (`:53`), with `Exec="<APPIMAGE or the app executable>" --hidden`; turning it off deletes that one file (`:87`). A path holding a quote, `$`, `%`, a backslash or a line break is refused rather than escaped (`:9`). A failure reaches Settings as `desktop/autostart-failed` and the toggle stays off. `--hidden` creates the window without showing it (`ui/packages/main/src/browser-window-factory.ts:36`), so the renderer still runs its existing worker auto-start when Discord or Slack is set up.
- **Notifications**, only where Electron supports them, title "Scout", fixed text (`ui/packages/main/src/shell/notifications.ts:24`): the supervisor gave up on the worker, which it reports as a `crashed` status with `gaveUp` set (`ui/packages/main/src/worker/supervisor.ts:133`): "Scout stopped and could not restart. Open Scout to start it again." after the worker had run, "Scout could not start." when it never ran since the last start; a crash it is still restarting from notifies nothing; on Linux, a `--hidden` start ("Scout is running in the background. Open it from the app menu or the tray.", `ui/packages/main/src/shell/notifications.ts:51`); a pairing completed ("<user id> can now use Scout", only when a code was pending and the id was added); the first time the window is hidden to the tray ("Scout is still running in the tray." on macOS and Windows, "Scout is still running. Open it again from the app menu or the tray." on Linux; once, remembered under `app.tray-notice-shown`). None carries a message, a transcript, a token, a pairing code or a log line.
- **Classification** ([[pages/decisions/0009-descriptive-vs-executive-writes]]). The login item and the autostart file are Scout's own startup entry: written when the owner flips the toggle in the desktop UI and, amended 2026-11-02 by [[pages/decisions/0031-desktop-self-update]], refreshed at start-up to Scout's own current executable when an entry already exists (an AppImage update can rename the file); never reachable from the worker, the LLM or chat, and limited to that one entry. They are Scout's own configuration, like the vault, not an executive write. The tray and the notifications are local UI.

## Consequences

- **Better:** the bot keeps answering after the window is closed and can come back after a reboot with no window; the owner sees from the tray whether the worker runs and can stop, start or quit it there.
- **Worse:** Scout keeps a worker process and a hidden renderer resident; quitting now needs Quit Scout in the tray or the app menu. On a Linux desktop with no StatusNotifier host (GNOME without an AppIndicator extension), Electron creates the tray without an error, so the no-tray fallback does not trigger and the icon is simply not shown; Scout can then run with no visible window. Mitigation, chosen over detecting the tray through DBus: on Linux a hidden start always shows "Scout is running in the background. Open it from the app menu or the tray.", a Linux desktop without notifications never starts hidden, and the first-close notice there names the app menu; relaunching Scout from the app menu shows the existing window. The gap remains for an owner who dismisses those notifications and has no tray.
- **Neutral but noteworthy:** macOS login items do not pass `args`, so a login launch there opens the window. Linux uses `~/.config/autostart` and ignores `XDG_CONFIG_HOME`. Run from source, the autostart entry points at the Electron binary, so it is only useful from an installed build.

## Alternatives considered

- **Always keep running, no setting.** PR 45 did this; with no tray on a desktop it leaves an invisible process.
- **A public vault key the renderer writes.** The renderer would bypass main's cached value, which `window-all-closed` reads synchronously; one channel per toggle keeps main the only writer.
- **A launch agent, systemd user unit or Windows service.** More resident than needed for level 1, and a second lifecycle to supervise.
- **Status quo.** Closing the window stops the bot.

## Enforcement

- `ui/packages/main/test/shell-app-lifecycle.test.ts`: keep running on or off, Quit Scout through `before-quit`, second launch, `--hidden`.
- `ui/packages/main/test/tray.test.ts`: menu, labels, enabled state per worker state and while stopping, tooltip, Ask Scout..., tray creation failure.
- `ui/packages/main/test/shell-login-item.test.ts` and `ui/packages/main/test/ipc-desktop.test.ts`: login item arguments, the exact Linux entry and its mode, removal of that file only, the failing write, the forced-off keep running.
- `ui/packages/main/test/shell-notifications.test.ts`: the three notifications and their exact text.
- `ui/packages/renderer/src/app/wizard/settings/settings.component.spec.ts`, Settings: Desktop.
- Invariants: [[pages/invariants/no-executive-writes]], [[pages/invariants/secrets-never-in-source]].

## Related

- Services affected: [[pages/services/desktop-ui]]
- Related decisions: [[pages/decisions/0009-descriptive-vs-executive-writes]], [[pages/decisions/0021-allowed-users]]

## Notes

Out of scope: the quick-ask window, a global shortcut and push-to-talk (separate change); a macOS template tray image.
