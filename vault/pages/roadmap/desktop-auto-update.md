---
title: "Desktop update notice and one-click update"
version: "1.0"
date: 2026-11-02
changes: "Initial initiative page: version check against GitHub Releases, what's new, user-approved update"
page_type: roadmap
status: active
description: "Initiative: Scout Desktop learns from GitHub Releases that a newer version exists, shows what is new, and installs it only after the user says yes; AppImage and Windows first, macOS after code signing."
source:
  - chat
  - ".github/workflows/release.yml"
  - "ui/build/electron-builder.yml"
reliability: medium
---

# Desktop update notice and one-click update

> When a newer Scout Desktop is on GitHub Releases, the app says so at start-up, shows what changed, and, if the user agrees, downloads and installs it without a trip to the download page.

## Why now

The owner asked on 2026-11-02, right after the first release pipeline landed ([[pages/decisions/0029-project-site-and-releases]]): "identify whether another version exists, update automatically instead of downloading by hand; on open, show that a new version exists and what is new, and let the user allow the update". Releases now carry a tag that is the version, generated notes, deterministic asset names and checksums, which is everything an update check needs. Without this, every fix the owner tests becomes a manual download, and users of the site's packages never learn a fix shipped.

## Scope

- **In scope:** a version check against the latest GitHub Release at start-up and once a day; a notice in the main window and the tray with the new version and its release notes; an "Update and restart" action that downloads the matching package, verifies it and installs it, only after the user clicks; "Later" and "Skip this version"; a Settings toggle for the check; the release workflow keeping the metadata the updater needs.
- **Out of scope:** silent or forced updates; updating the `.deb` in place (no updater supports it; the notice offers the download instead); macOS one-click update before code signing and notarization exist ([[pages/roadmap/followups]], "Desktop builds are unsigned"); delta updates beyond what the updater does by itself; a separate update server.
- **Anti-goals:** a download or an install that the user did not just approve; an update path the model or the worker can trigger; a check that sends anything about the machine beyond the request GitHub sees anyway.

## Where it sits against Rule A

Replacing Scout's own executable is a write to the host filesystem, so it needs its own ADR before phase 2 ([[pages/invariants/no-executive-writes]] says boundary cases default to forbidden). The proposed classification: the update is an action on Scout's own installation, performed by the desktop main process, started by a click in the desktop and never reachable from a chat message, a function the model can call, or the worker. The check itself is a GET of public release metadata, the same request the project site makes.

## Design

### Phase 1: the notice (works for every package)

- Main compares `app.getVersion()` with the `tag_name` of `GET https://api.github.com/repos/vitorafgomes/scout/releases/latest` (unauthenticated, one request, 60 per hour per IP is far above a start-up plus a daily timer). Semantic comparison, no string compare; a release with no asset for this platform is not offered.
- The main window shows a bar: "Scout 0.2.0 is available (you have 0.1.0)" with **What's new** (the release body, rendered as plain text and simple lists, links opened in the system browser), **Download** (opens the asset for this platform and architecture in the browser: AppImage or .deb on Linux, .dmg on macOS, .exe on Windows), **Later** (asks again next day) and **Skip this version** (stored, asked again only for a newer one). The tray shows a dot and a "Update available" item that opens the same bar.
- Settings, Desktop section: "Check for new versions" (on by default) and "Last check: date, result".
- Dev builds (`app.isPackaged` false) never check.
- Storage: `update.skippedVersion` and `update.lastCheck` under main-only vault keys, next to `app.keep-running` ([[pages/services/desktop-ui]]).

### Phase 2: one-click update (AppImage and Windows)

- `electron-updater` with the GitHub provider, `autoDownload: false`, `autoInstallOnAppQuit: false`. It reads `latest-linux.yml` and `latest.yml` from the release, so `.github/workflows/release.yml` stops deleting `*.yml` and `*.blockmap` for the AppImage and the NSIS installer; the site's `pickDownloads` already ignores them ([[pages/decisions/0029-project-site-and-releases]]). `electron-builder.yml` gains `publish: { provider: github, owner: vitorafgomes, repo: scout }` so the metadata files are generated; `--publish never` stays, because the workflow publishes with `gh`.
- **Update and restart** replaces **Download** when the updater supports the current package: AppImage (the updater rewrites the AppImage file in place; it needs the `APPIMAGE` environment variable the AppImage runtime sets) and Windows NSIS (per-user install, unsigned builds update; SmartScreen may prompt once). Progress shows in the bar; the updater checks the SHA-512 from the metadata before installing; `quitAndInstall` runs only after the download succeeded and the user clicked.
- **macOS:** Squirrel.Mac refuses unsigned updates, so the dmg keeps the Download button until the builds are signed and notarized; then the same flow applies with `latest-mac.yml`.
- **.deb:** Download button only; a later ADR may add an apt repository.
- **Failing-call tests (Rule I):** a 404 (no release), a 403 (rate limit), a timeout, a corrupt download (checksum mismatch) and a release without an asset for the platform each leave the app running on the current version with a one-line message.

### Interaction with the running instance

The X11 relaunch and the single-instance lock ([[pages/decisions/0028-local-quick-ask-assistant]]) mean the installed update starts the way the AppImage already restarts itself: the updater's `quitAndInstall` exits the app and launches the new file; the Wayland relaunch guard must see the new executable path. One test covers it.

## Target outcome

By the end of phase 2, the owner installs 0.1.0 from the site, a 0.2.0 tag is pushed, and on the next start Scout shows "0.2.0 is available" with its notes; one click later Scout 0.2.0 is running from the same AppImage path, with no download page visited.

## Status

- **As of 2026-11-02:** plan written, nothing built. Phase 1 can start without any decision from the owner; phase 2 needs the ADR and the workflow change.

## Phases

### Phase 1: the notice
- **Target:** one owner test round
- **Scope:** version check, bar with what's new, Download, Later, Skip, Settings toggle, tray item, vault keys.
- **Success criteria:** with a fake "latest" answer one version ahead the bar shows the notes and the right asset link per platform; with the API down the app starts with no bar and a log line; Skip hides that version only; the check never runs in a dev build (tests for each).

### Phase 2: one-click update
- **Target:** after the owner approves ADR 0031
- **Scope:** `electron-updater`, metadata kept in the release, Update and restart for AppImage and Windows, progress, checksum, the five failing-call tests, the relaunch test.
- **Success criteria:** the target outcome above on the owner's Pop!_OS; the same on a Windows machine or VM; macOS and .deb still show Download.

### Phase 3 (after signing)
- macOS one-click update once the builds are signed and notarized.

## Decisions pending

- `pages/decisions/0031-desktop-self-update` (to be drafted at phase 2): the Rule A classification above, the updater, which packages, what the user must click, what is stored.
- **Owner:** confirm that phase 1 ships before phase 2; whether to keep the metadata files in the release from the next tag on (they are harmless to the site).

## Services affected

- [[pages/services/desktop-ui]]: main-process updater, the bar, Settings, tray, vault keys.
- `.github/workflows/release.yml` and `ui/build/electron-builder.yml`: metadata kept, publish provider configured.
- [[pages/runbooks/release]]: the notes body becomes what users read; the runbook gains a line on writing them.
