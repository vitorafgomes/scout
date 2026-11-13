---
title: "Desktop GitHub card: Scout bot (optional): Name, handle and the mention"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Desktop GitHub card service page."
page_type: service
status: active
description: "Read when a change touches: Name, handle and the mention; The GitHub card sits in the."
source: - ui/packages/main/src/oauth/github-auth.ts - ui/packages/main/src/oauth/github-bot.ts - ui/packages/main/src/ipc/github.ts - ui/packages/main/src/ipc/github-verdict.ts - ui/packages/main/src/notifications/github-activity.ts - ui/packages/renderer/src/app/features/github-connect.component.ts - pages/services/desktop-ui
reliability: high
---

## Scout bot (optional)

**Name, handle and the mention.** The owner renamed the bot on GitHub and expected the desktop to show it and to offer the change (2026-11-08). Once the bot exists, created or installed, the bot section shows "Name: <name>" ("not read yet" for a bot kept before names were read and not read since) and "Handle: @<slug>", the `nameError` line under them when the last read of the App failed (`ui/packages/renderer/src/app/features/github-connect.component.ts:362`), then "Ask for a review with @<slug>", the slug taken from the status's `<slug>[bot]` login, a Copy button labelled "Copy @<slug>" that writes it with `navigator.clipboard.writeText`, as the device code's Copy does, and "Mention it in a pull request's description or a comment; the bell tells you who asked." (`ui/packages/renderer/src/app/features/github-connect.component.ts:356`, `mentionHandle` `:920`). The slug is the App's, never what the owner types: a handle that is not the kept slug (an App created as `Scout (<login>)` has the slug `scout-<login>`) finds nothing, and this line is how the owner learns the right one. Tests: `ui/packages/renderer/src/app/features/github-connect.component.spec.ts:913` (shown only for created and installed), `:923` (the copy follows a renamed slug). Beside Add a picture on GitHub, "Change the name on GitHub" opens the same `settingsUrl` through `scout:app.openExternal`, because GitHub offers no API to rename an App, with "GitHub renames an App only on its settings page; Scout reads the new name when this card opens again or when you click Refresh."; Refresh reads the GitHub status and the bot again (`reread`, `ui/packages/renderer/src/app/features/github-connect.component.ts:926`), so a name and handle changed on GitHub show at once and the mention line uses the new handle (`mentionHandle`, `ui/packages/renderer/src/app/features/github-connect.component.ts:920`, `:365`). Tests: `ui/packages/renderer/src/app/features/github-connect.component.spec.ts:950` (the name and handle shown), `:969` (Change the name on GitHub opens the user's or the organization's settings page), `:992` (Refresh shows the new name and handle and the mention with the new handle), `:1015` (a failed read keeps the last name with its line).

The GitHub card sits in the Features grid, so it is hidden with the grid when the control plane is not running, although the sign-in does not need it.
