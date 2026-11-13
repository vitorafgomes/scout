---
title: "Desktop GitHub card"
version: "1.16"
date: 2026-11-07
updated: 2026-11-12
changes: "v1.16 (2026-11-12): the bot lists every repository of its installation, page after page up to 50 pages, and keeps the last full list when a later page fails; an OAuth wait whose signal is already aborted is cancelled at once with no listener opened."
page_type: service
status: active
description: "The GitHub card on the desktop's Features screen and the GitHub calls main makes for it: the owner's sign-in or token, the optional Scout bot, and the verdict the owner gives from a review card."
source:
  - ui/packages/main/src/oauth/github-auth.ts
  - ui/packages/main/src/oauth/github-bot.ts
  - ui/packages/main/src/ipc/github.ts
  - ui/packages/main/src/ipc/github-verdict.ts
  - ui/packages/main/src/notifications/github-activity.ts
  - ui/packages/renderer/src/app/features/github-connect.component.ts
  - pages/services/desktop-ui
reliability: high
---

# Desktop GitHub card

> Main owns every GitHub credential the desktop holds and every GitHub call it makes; the renderer sees statuses, never a token or a key.

## Responsibility

Owns the GitHub card on the Features screen, the owner's GitHub session or token, the Scout bot's App key and installation token, and following a verdict the owner gives from a review card. Does not own the worker's GitHub functions ([[pages/services/plugin-github]]), the review card itself ([[pages/services/desktop-quick-ask]]) or the approval queue ([[pages/services/control-plane]]).

## Rules

The rules this project must keep, and what to read before changing it, are in `.claude/rules/desktop-github.md`. Claude Code loads that file when a task touches the project.

## Topics

Read the page your change touches, not all of them.

| Page | Read when a change touches | Characters |
|---|---|---|
| [[pages/services/desktop-github/public-contract\|Public contract]] | Public contract | 1442 |
| [[pages/services/desktop-github/how-it-works-mermaid\|How it works: mermaid]] | mermaid; The GitHub card does not use; GitHubAuth (ui/packages/main/src/oauth/github-auth.ts:98) runs the device flow,; No client id; Use a token; Pending; Connected; Failed | 8415 |
| [[pages/services/desktop-github/how-it-works-repositories-the-app-is-installed-o\|How it works: Repositories the App is installed on]] | Repositories the App is installed on | 5721 |
| [[pages/services/desktop-github/scout-bot-below-connect-the-card-offers-use\|Scout bot (optional): Below Connect the card offers "Use]] | Below Connect the card offers Use; Picture; Name and slug follow a rename | 12530 |
| [[pages/services/desktop-github/scout-bot-name-handle-and-the-mention\|Scout bot (optional): Name, handle and the mention]] | Name, handle and the mention; The GitHub card sits in the | 3082 |
| [[pages/services/desktop-github/verdict-from-the-review-card\|Verdict from the review card]] | Verdict from the review card | 3718 |
| [[pages/services/desktop-github/github-activity-for-the-bell\|GitHub activity for the bell]] | GitHub activity for the bell | 7426 |
| [[pages/services/desktop-github/mentions-of-the-scout-bot\|Mentions of the Scout bot]] | Mentions of the Scout bot | 4831 |
| [[pages/services/desktop-github/hot-spots\|Hot spots]] | Hot spots | 770 |
| [[pages/services/desktop-github/testing-footprint\|Testing footprint]] | Testing footprint | 1329 |

## Related

[[pages/services/desktop-ui]], [[pages/services/plugin-github]], [[pages/services/desktop-quick-ask]], [[pages/services/control-plane]]; dated changes in [[pages/history/desktop-github]].
