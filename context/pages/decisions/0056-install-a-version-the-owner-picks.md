---
title: "ADR 0056 - The owner may install a version picked from the published ones, an older one only after confirming it"
version: "1.0"
date: 2026-11-14
updated: 2026-11-14
changes: "v1.0: the decision as first written"
page_type: adr
status: accepted
description: "Settings, Updates lists the published versions on the owner's channel and installs one on the owner's click; main installs only a version it listed, lets the updater go down for that install only when the version is older, and the renderer asks first for an older one."
supersedes: ""
superseded_by: ""
source:
  - chat
  - context/prototypes/desktop/Settings.dc.html
  - ui/packages/main/src/update/update-notice.ts
  - ui/packages/renderer/src/app/wizard/settings/settings.component.ts
reliability: high
---

# ADR 0056 - The owner may install a version picked from the published ones, an older one only after confirming it

> Settings, Updates lists the published versions on the owner's channel and installs one on the owner's click; main installs only a version it listed, lets the updater go down for that install only when the version is older, and the renderer asks first for an older one.

## Status

Accepted on 2026-11-14 by the owner, who approved the artboard that draws it and asked for the code. It amends [[pages/decisions/0031-desktop-self-update]], which installed only the release the notice offered and never went down.

## The problem

The owner asked to choose which version to install, an older one included, to test it. [[pages/decisions/0031-desktop-self-update]] allowed one install: the newest release on the owner's channel, through the notice.

## The decision

- **The list.** `scout:update.listVersions` (`ui/packages/main/src/update/update-notice.ts:575`, `listVersions`, `:343`) reads the published versions on the owner's channel, newest first: no draft, and on Stable no pre-release, marked by GitHub or by a suffix in its tag. A list GitHub does not give answers no versions and the kind of the failure, and a list read on a channel the owner has since left answers nothing.
- **Only a listed version.** `scout:update.installVersion` (`:582`) installs only a version of the last list main gave, never the running one, and only where the package can replace itself; anything else answers a failed progress and touches nothing. A failed list and a channel change forget the earlier list.
- **Down for that install only.** The updater reads the chosen release's own metadata, the `latest*.yml` under its tag, and may go down only when that version is older than the running one (`:400`); whatever happens, it is pointed back at the GitHub feed with going down off (`:405`). The checksum and the same-version check of [[pages/decisions/0031-desktop-self-update]] still apply.
- **Asks first.** Settings asks "Install Scout <version>, an older version?" with Cancel focused before an older install, and before any install when the running version is unknown (`ui/packages/renderer/src/app/wizard/settings/settings.component.ts:1664`). Where the package cannot replace itself the button is Download and opens that version's page.

## Calls that leave the process

- `GET https://api.github.com/repos/vitorafgomes/scout/releases?per_page=30`, unauthenticated, read: the list, the same request as the Beta check.
- The updater's reads of `https://github.com/vitorafgomes/scout/releases/download/<tag>/latest*.yml` and the package: reads.
- Replacing Scout's own executable with the chosen version: the write of [[pages/decisions/0031-desktop-self-update]], on the owner's click, after a confirmation when older. No feature grant applies: it is Scout's own installation, out of reach of the worker and the model.

## What follows

- **Gained:** the owner can try any published version, and go back.
- **Paid:** an older version may not read everything a newer one saved; the dialog says so, and nothing is deleted.
- **To watch:** the list shows the 30 most recent releases only.

## Rejected options

- **A free version field:** any string could reach the updater; a list main itself gave cannot.
- **Leaving going down on:** it would let the notice's install go down too; it is on for one install only.

## How it is held

- The "a version the owner picks" block of `ui/packages/main/test/update-notice.test.ts:1229`: the list per channel, each failing call, a refused unlisted, excluded, running or missing version, a stale list after a failed list or a channel change, going down only for an older install, the feed put back after a success and after a failure.
- The "Install a specific version" specs of `ui/packages/renderer/src/app/wizard/settings/settings.component.spec.ts`.
- The write-verb scan of `ui/packages/shared/test/ipc-contract.test.ts`, which names `scout:update.installVersion` with its reason.

## Links

- Projects it shapes: [[pages/services/desktop-channels/update-notice]]
- Invariants it leans on: [[pages/invariants/no-executive-writes]]
- Decisions it amends: [[pages/decisions/0031-desktop-self-update]]

## Amendments

None yet.
