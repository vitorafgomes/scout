---
title: "Electron Provisioning UI - feasibility study: Desktop-app detection across OSes"
version: "1.0"
date: 2026-04-22
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: longform
status: active
description: "Whether Scout can detect installed Discord, Slack or Teams desktop clients across operating systems, and why detection is not required for provisioning."
source:
  - "obsidian-vault/scout/02-architecture/electron-provisioning-ui.md"
reliability: medium
---

# Electron Provisioning UI - feasibility study: Desktop-app detection across OSes

### Do we actually need it?

Probably **no**, for Scout's use case. The desktop client's presence
tells us the user *might* use that platform, but provisioning a bot
does not require the desktop client to be installed — a web session is
enough for all three platforms. Detection is UX polish: "you already
use Slack, want to add Scout there?" rather than a hard requirement.

The minimum viable UI could just list Discord / Slack / Teams as three
buttons and let the user decide. Detection becomes a "detected on this
machine" badge, not a gate.

If we do implement detection:

### Windows

- **Installed-apps registry:** enumerate
  `HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall` and the
  `Wow6432Node` mirror + `HKCU` equivalents. Match `DisplayName`.
- **App Paths:** `HKLM\Software\Microsoft\Windows\CurrentVersion\App Paths\`
  is the canonical "where is this exe" table for classic installers.
- **AppX (Store) apps:** use
  `Get-AppxPackage` via PowerShell subprocess, or parse
  `%LOCALAPPDATA%\Packages\`. New Teams and Store-installed Slack live
  here.
- **URI schemes:** `HKCU\Software\Classes\<scheme>\` — cheap check that
  a client claims e.g. `discord://`.

### macOS

- **Launch Services:** `lsregister -dump` (or the
  `CoreServices.LaunchServices` framework via native bindings) returns
  all registered apps with bundle ids. More reliable than globbing
  `/Applications`.
- **Spotlight:** `mdfind "kMDItemCFBundleIdentifier == com.hnc.Discord"`
  works from any user shell.
- **Bundle identifiers to match:** `com.hnc.Discord`,
  `com.tinyspeck.slackmacgap`, `com.microsoft.teams` /
  `com.microsoft.teams2`.

### Linux

- **`.desktop` files:** walk `~/.local/share/applications/`,
  `/usr/share/applications/`, `/var/lib/flatpak/exports/share/applications/`,
  `/var/lib/snapd/desktop/applications/`. Parse `Name=` and `Exec=`.
- **Package managers:** `dpkg -l`, `rpm -q`, `flatpak list`,
  `snap list`. All are exec subprocesses.
- **`which` fallback:** `which discord`, `which slack`, `which teams`.
- **Reality check:** Microsoft does not ship a Linux Teams desktop. On
  Linux the Teams button should point to the PWA.

### Libraries

- No mature cross-platform Electron-ready "is app X installed" library
  exists. Ad-hoc scripts (e.g.
  [cross-platform Electron detector gist](https://gist.github.com/MohamedElashri/9460f7b1d139ce710af83801fb504928))
  exist for the reverse problem (find Electron apps). For our case,
  **roll our own thin per-OS module**: `detect/windows.ts`,
  `detect/macos.ts`, `detect/linux.ts`, each exporting a small set of
  predicates (`isDiscordInstalled()`, etc.).
- The npm package `detect-installed` is about *npm packages*, not
  desktop apps. Do not use.
