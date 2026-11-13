---
title: "ADR 0031 - Scout Desktop updates itself only on the user's click, and only its own installation"
version: "1.0"
date: 2026-11-02
changes: "Initial ADR"
page_type: adr
status: accepted
description: "The desktop may replace its own executable with a GitHub Release the user chose by clicking Update and restart; the model and the worker never reach that path; macOS and .deb keep a download link until signing or a package repository exist."
supersedes: ""
superseded_by: ""
source:
  - chat
  - ".github/workflows/release.yml"
  - "ui/build/electron-builder.yml"
  - "ui/packages/main/src/update/update-notice.ts"
reliability: high
---

# ADR 0031 - Scout Desktop updates itself only on the user's click, and only its own installation

> Replacing Scout's own executable is the one write to the host that Scout performs, and it happens only when the user clicks Update and restart in the desktop, for a release GitHub published, after the download's checksum matched; nothing else in Scout, and nothing the model or the worker can call, reaches that path.

## Status

- [ ] Proposed
- [x] Accepted
- [ ] Deprecated / superseded by

Updated: 2026-11-02. Accepted on the owner's instruction after testing the phase 1 notice: "I should not have the download step, but an update instead".

## Context

Scout is read-only by contract. [[pages/invariants/no-executive-writes]] and [[pages/decisions/0009-descriptive-vs-executive-writes]] forbid any write that mutates code, infrastructure, configuration, data or the host filesystem outside Scout's own configuration, and say a boundary case defaults to forbidden. Overwriting the running AppImage, or running a Windows installer, is such a boundary case: the target is Scout's own installation, but it is a file on the host.

Phase 1 of [[pages/roadmap/desktop-auto-update]] gave the desktop a version check against the latest GitHub Release, a bar with the release notes and a Download button that opened the package in the browser. The owner tested it and asked for the update itself, not a download. The release pipeline ([[pages/decisions/0029-project-site-and-releases]]) already publishes deterministic packages with checksums on every `v*` tag, which is what an updater consumes.

Constraints: the builds are unsigned ([[pages/roadmap/followups]], "Desktop builds are unsigned"); macOS refuses to apply an unsigned update, and no updater handles a `.deb` in place. The desktop main process is the only place that may act, the renderer only asks, and the worker and the model have no channel to it.

## Decision

- **Who acts.** The desktop main process, through `electron-updater` with the GitHub provider, and only inside the handler of the `scout:update.install` channel, which the renderer invokes when the user clicks **Update and restart**. The updater runs with `autoDownload`, `autoInstallOnAppQuit` and `allowPrerelease` off, so nothing is fetched or applied without that click; the daily check of phase 1 stays a read and never downloads.
- **What is written.** Only Scout's own installation: the AppImage the user runs, or the per-user Windows installation through the NSIS installer. On Linux the desktop offers the update only when `APPIMAGE` and `APPDIR` are set by the AppImage runtime and the running executable lives inside `APPDIR`, because a `.deb` Scout started from inside another AppImage program inherits that program's `APPIMAGE` and the updater would otherwise delete the other program's file. The updater writes the new release next to the old file under the release's own name and removes the old one, so the path can change; the start-at-login entry is refreshed to the current path on the next start. The updater verifies the SHA-512 recorded in the release metadata before installing; a mismatch installs nothing and the bar says so. The release the updater offers must be the same version the notice showed, or nothing is installed.
- **Where it does not apply.** macOS keeps the Download link until the builds are signed and notarized; `.deb` keeps it until an apt repository exists. The bar offers Download in those cases and never pretends to update.
- **What the release carries.** `ui/build/electron-builder.yml` declares the GitHub publish provider so `electron-builder` embeds `app-update.yml` and writes `latest-linux.yml`, `latest.yml` and the `.blockmap` files; `.github/workflows/release.yml` keeps and uploads them with the packages (it still publishes with `gh`, never through electron-builder, and still drops the Intel Mac image). The project site ignores those files ([[pages/decisions/0029-project-site-and-releases]]).
- **What the user sees.** Progress for checking, downloading (with percent), ready, installing, or a one-line failure that leaves the buttons back, adds a Download link for the file, and keeps the current version running. Before the new version starts, the desktop stops the worker and releases its single-instance lock, so the new process can take over while the old one finishes exiting. Later and Skip this version keep their phase 1 meaning.
- **What never happens.** No background download, no install on quit, no update from a prerelease or a draft, no update when the API, the metadata or the checksum disagree, no token: the repository is public and the updater is configured without credentials.

## Consequences

- **Better:** a fix the owner tests becomes an update the user accepts in one click, with the release notes in front of them; the version they run is the version GitHub published and checksummed; the write surface is one handler, one click, one file.
- **Worse:** the release must carry the updater metadata from now on, so a release made by hand without it leaves users on Download; macOS and `.deb` users keep downloading until signing or a repository exist; an unsigned Windows update may show a SmartScreen prompt.
- **Neutral but noteworthy:** `scout:update.install` and `scout:update.progress` are named in the write-verb allow-list of the IPC contract test with this justification; the five phase 1 channels stay reads and vault writes. A failed check or install is logged at info or warn, never as an error, because the app keeps running as it was.

## Alternatives considered

- **Keep Download only.** Rejected by the owner after testing; it moved the work to the user and left them a browser tab and a file to replace by hand.
- **Silent updates on quit** (`autoInstallOnAppQuit`). Rejected: a write the user did not just approve, and the plan's anti-goal.
- **Scout's own downloader and installer.** Rejected: re-implementing checksum verification, AppImage replacement and the NSIS hand-off that `electron-updater` already does, with more surface to review.
- **Publishing through electron-builder** instead of `gh`. Rejected: the workflow keeps one publisher, and electron-builder would need a token with write access in every build leg ([[pages/decisions/0029-project-site-and-releases]] gives `contents: write` to the publish job only).

## Enforcement

- `ui/packages/main/test/update-notice.test.ts`: the updater options are asserted off; `quitAndInstall` is called once and only after `update-downloaded`; a sha512 error, a different version, a network failure, a timeout, a 404 and a 403 install nothing; `canInstall` is true only for an AppImage and for Windows; a second click during an install is ignored.
- `ui/packages/shared/test/ipc-contract.test.ts`: the write-verb scan names the two install channels with the reason above and refuses any other write-sounding channel.
- The review gate's `read-only` lens on every change to `ui/packages/main/src/update/`.
