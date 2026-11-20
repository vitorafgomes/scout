---
title: "Desktop GitHub card: Testing footprint"
version: "1.2"
date: 2026-11-13
updated: 2026-11-20
changes: "v1.2 (2026-11-20): the tests of the review notes and review inputs. v1.1 (2026-11-19): the tests of the waiting approval, with a conflict and a mergeability GitHub has not said. v1.0: split out of the Desktop GitHub card service page."
page_type: service
status: active
description: "Read when a change touches: Testing footprint."
source: - ui/packages/main/src/oauth/github-auth.ts - ui/packages/main/src/oauth/github-bot.ts - ui/packages/main/src/ipc/github.ts - ui/packages/main/src/ipc/github-verdict.ts - ui/packages/main/src/notifications/github-activity.ts - ui/packages/renderer/src/app/features/github-connect.component.ts - pages/services/desktop-ui
reliability: high
---

## Testing footprint

`ui/packages/main/test/oauth-github.test.ts` (with the count of the App's repositories), `oauth-github-token.test.ts`, `oauth-github-bot.test.ts`, `ipc-github.test.ts`, `ipc-github-verdict.test.ts` (the click sent once, the 2-second follow pushing each change once and stopping when given or failed, an unreachable control plane, five failed reads, eight malformed requests refused before any call, the status channel, shutdown, one action per pull request and commit across clicks and windows (`ui/packages/main/test/ipc-github-verdict.test.ts:323`, `:360`), the lookup for a later window (`:380`) and the push to every open window), `github-waiting-approvals.test.ts` (the approval that waits for the checks, [[pages/services/desktop-github/verdict-from-the-review-card]]: given once every check passed, each ending, each read that fails; a pull request with conflicts ends as `conflict` at the first read that shows it, gives nothing and reads no check, also while checks run, `ui/packages/main/test/github-waiting-approvals.test.ts:354`; while GitHub has not said whether it can be merged it keeps waiting, is given at the next read that says so, and ends after two hours, `:400`), `notifications-sources.test.ts` (one row per bell entry of a waiting approval, the conflict at `ui/packages/main/test/notifications-sources.test.ts:486`), and `ui/packages/renderer/src/app/features/github-connect.component.spec.ts`.

- **2026-11-20, the card after a verdict and the note preview (ADR 0063):** in `review-findings.component.spec.ts`, the describe "a second verdict at the same commit and the note preview, as the artboard draws them" (eight specs: the given line, the card still offering, nothing new sent, a second verdict, the preview asked on selection and applied edit, the request for changes named, a failed preview, markup kept out of the DOM) and "the preview shows the note the worker composed"; the matrix updated on purpose for a card that stays open; `review-findings.spec.ts`, the shortcode spec. The renderer: 91 files, 5500 passed.
