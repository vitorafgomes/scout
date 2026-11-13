---
title: "ADR 0045 - The desktop's theme switch and notification center"
version: "1.6"
date: 2026-11-07
changes: "v1.6 (2026-11-12) a read job entry turns unread again only on a state change or a note, not at each area's end; an audit running when Scout closed comes back in progress, only a test run as failed."
page_type: adr
status: accepted
description: "Scout Desktop gets a light, dark or system theme chosen from a sun, moon and monitor switch in a new top bar, and a bell that collects what Scout does in the background, what it wrote on GitHub, and where the owner was mentioned or a review of Scout's got a comment, read from GitHub every five minutes."
supersedes: ""
superseded_by: ""
source:
  - chat
  - ui/packages/renderer/src/styles.scss
  - ui/packages/main/src/shell/notifications.ts
  - ui/packages/main/src/oauth/github-auth.ts
  - ui/packages/main/src/notifications/voice-notice.ts (amendment v1.2, 2026-11-07)
  - ui/packages/main/src/notifications/worker-health.ts (amendment v1.3, 2026-11-08)
  - ui/packages/main/src/notifications/pending-approvals.ts (amendment v1.3, 2026-11-08)
  - ui/packages/main/src/notifications/github-activity.ts (amendment v1.4, 2026-11-08)
  - ui/packages/main/src/notifications/job-controls.ts (amendment v1.5, 2026-11-11)
reliability: high
---

# ADR 0045 - The desktop's theme switch and notification center

> A top bar on every main-window screen carries a theme switch (light, dark, system) and a bell; the bell lists the background work, the writes Scout made and the GitHub mentions and comments that concern the owner, kept in memory and in the desktop's vault, with nothing sent anywhere.

## Status

- [x] Proposed
- [x] Accepted

Updated: 2026-11-07. The owner approved the prototype ("pode implementar o protótipo", translated: "you can implement the prototype") of https://claude.ai/artifact/FgdMUEtiXb3fRSSK8mHjYz.

## Context

The owner, 2026-11-07 (translated): "everything is dark only, but there should be an option to turn dark on and off, with the moon and the sun", and "we could also add a bell for notifications: processes running in the background, or when we commented on something or were mentioned somewhere, so we can see what is happening". The screen review of the same day found the theme fixed in code: `color-scheme: dark` and `theme-type: dark` set unconditionally on `html` (`ui/packages/renderer/src/styles.scss:5`, `:9`), about 1,000 colour literals across 41 component files, and notices that live four seconds in a snackbar with no history. The desktop already raises a few native notifications (`ui/packages/main/src/shell/notifications.ts:16`) and keeps small settings in its safeStorage vault.

## Decision

- **Theme.** Three choices, Light, Dark and System (the operating system's preference, followed live), stored in the vault under the main-only key `app.theme`, default System. Main applies it to every window (main, quick ask, floating button, operator) through `nativeTheme.themeSource` and an IPC push; the renderer sets `data-theme` on `html`. `styles.scss` declares the Material theme for both schemes (`theme-type: color-scheme` with light and dark override blocks) and every `--sc-*` token gets a light and a dark value; the missing tokens the review names are added (ok, warn, error and info container pairs, on-accent, code background, mono font, scrim, focus ring, active navigation). Replacing each screen's literals with tokens is a mechanical pass per screen, done with each screen's migration; until a screen is migrated it keeps its look in both themes.
- **Top bar.** A shell component above every main-window screen: the screen's title, the AI Source and model in use, a search placeholder kept out of scope until a search exists, the theme switch (three icon buttons with labels for screen readers and tooltips) and the bell.
- **Icons.** One inline SVG icon component, `sc-icon`, with a small registry of named paths drawn on a 24 grid, used by every button with an icon and text; no icon font and no new dependency (the renderer's content security policy blocks remote fonts).
- **Notification center, in main.** A main-process store keeps at most 200 notifications, newest first, each with an id, a kind (running, done, failed, paused, github), a title, a detail line, an optional progress, an optional link, a time and a read flag, persisted in the vault under `app.notifications`, and pushed to every window. Its sources:
  - the worker's proactive notices (the audit job's batches, pauses and end, the verdict executor's outcome, a container test run), which main already receives as notice turns;
  - the verdict actions main follows (queued, given, failed);
  - the snackbar notices, which are also recorded;
  - **GitHub**, while the GitHub feature is connected: main reads `GET /notifications` with the owner's token every five minutes (with `If-Modified-Since` and the `X-Poll-Interval` GitHub sends), keeps only the reasons `mention`, `team_mention`, `review_requested`, `comment` and `author`, and records one entry per thread with its subject and link. This is a read that leaves the process, declared on the GitHub feature's `Notifications` row (Read) in the catalog, the same token and the same custody as the device-flow session ([[pages/decisions/0019-github-app-device-flow]]); it needs the App's or the token's Notifications read access, and without it the bell says once "GitHub notifications need the Notifications read permission" and stops polling.
- **Bell.** A badge with the unread count; a panel with filters (All, In progress, GitHub, Done), Mark all as read, each entry's link opening in the browser through the existing external-link path; the panel never runs an action by itself (Continue the audit asks through the chat).

## Amendment v1.1 (2026-11-07): GitHub activity through the search API

GitHub's `GET /notifications` supports only classic personal access tokens (with the `notifications` or `repo` scope); the desktop's device-flow sign-in gives a GitHub App user token and the recommended pasted token is fine-grained, so the poll would always end on a 403. The bell reads GitHub activity through `GET /search/issues` instead, which every token type can call: every five minutes, for the signed-in login and `updated:>=` the last read, one query each for pull requests and issues that mention the owner (`mentions:`), pull requests waiting for the owner's review (`review-requested:`, open), and the owner's own pull requests and issues that got activity (`author:`, with comments); five queries a tick, well under the search API's thirty a minute. One entry per item, updated in place. A classic token with the `notifications` scope may still use `/notifications`; nothing depends on it. The catalog row reads "Search your GitHub activity every five minutes for the bell: mentions, review requests and replies on your pull requests and issues" and needs no extra permission beyond the repositories' reads.

## Amendment v1.2 (2026-11-07): actions on an entry

The owner, translated: "yes, include the notice in the bell". When a voice message arrives and no Whisper model is installed, the worker's reply in the chat stays, and the bell also gets one entry per hour of such messages, "A voice message could not be transcribed: no Whisper model is installed.", with two actions: "Download the base model (142 MB)", which main runs with the same Whisper download Local Models uses and whose progress and failure show in the entry, and "Open Models, Transcription", which the window runs. An entry carries its actions in a closed list main validates on load; main runs an action only on an entry it holds that offers it, with its arguments fixed in main (the variant is always `base`), and the download writes only under Scout's own `whisper-models` folder with the HuggingFace read already declared, so the bell still never writes outside Scout or acts on anything else. A model downloaded from either place marks the entry done; Scout uses it after its next restart. [[pages/services/desktop-ui]], [[pages/services/conversation]].

## Amendment v1.3 (2026-11-08): what becomes an entry

The Logs and bell review of 2026-11-08 found every on-screen notice ("Copied 3 lines", "Log cleared") recorded as an unread entry, taking one of the 200 places from GitHub and audit entries and raising the badge for something the owner had just seen; no entry for the two things that most need the owner, an action waiting for approval and a worker that stopped; and GitHub entries kept after GitHub was disconnected, describing an account Scout no longer reads. The manager adopted the review's recommendations on 2026-11-08, the owner may override them:

- **Notices.** Only an error notice the window shows is recorded; an info or success notice is shown and not kept. Background work keeps its entries as before (the worker's notices, the verdicts main follows, the voice message entry, main's restarts).
- **A pending approval.** Main reads the control plane's action queue every 15 seconds and after every decision, and keeps one entry, `approvals:pending`, while actions wait: "An action waits for your approval" or "N actions wait for your approval", the first three by action and target, and an Open Approvals action the window runs (it opens Approvals; main runs nothing). The entry is replaced when the queue changes, never stacked, and removed when nothing waits; a queue the control plane cannot answer keeps it as it was. It is a read of Scout's own control plane and never leaves the machine.
- **A worker that crashed.** One entry, `worker:health`: "Scout stopped unexpectedly and is restarting." while the supervisor restarts it after a crash, "Scout stopped and does not restart by itself." with where to start it again once the supervisor gives up, removed once the worker runs again or the owner stops it. A stop and start the owner or main asked for records nothing, and a first start that fails is left to the screen that started it.
- **GitHub Disconnect.** A connection change that leaves GitHub disconnected removes every GitHub entry and keeps the rest; a read still running from before the change records nothing.
- **Every page, under the search limit.** A read follows every page of each search (at most the search API's 1,000 results), so the five queries a tick of v1.1 become up to 50 searches on a busy account, over GitHub's 30 a minute. The poll sends at most 20 searches in any minute; a read that needs more goes on from the next page on the following tick and records its entries, and moves its window, only once every page answered.

The bell's other changes of the same review (opening a link or running an action marks that entry read, a read GitHub entry stays read unless its reason changed or its thread got more comments, a saved running entry comes back as failed) are behaviour on [[pages/services/desktop-ui]], Notification center.

## Amendment v1.4 (2026-11-08): a mention of the Scout bot

The owner, 2026-11-08 (translated): "the notifications must show that the bot was mentioned somewhere"; a team asks Scout for a review by mentioning the bot by its name in a pull request ([[pages/roadmap/future-improvements]], F21). GitHub's `mentions:` qualifier does not find an App's bot, and Scout's App is private, so its handle cannot be resolved through the public API; a phrase search does find it. Decision:

- **The query.** While the owner's Scout bot is installed, a read adds one search, `"@<slug>" in:comments,body is:pr is:open` scoped to the account the installation is on (`user:<login>` or `org:<org>`), with the read's `updated:>=` window. It is one more search inside the same 20-a-minute budget and resume; the slug is the one the desktop keeps and re-reads from the App ([[pages/decisions/0044-scout-acts-on-github-as-its-own-app]], amended 2026-11-08), never a fixed name.
- **Confirmation.** Phrase search is tokenised, so every candidate is confirmed before it becomes an entry: the pull request's body, its issue comments and review comments updated since the window, and its review summaries submitted in the window, read with the owner's token (read-only GETs that are not searches and do not count toward the 20). A text counts when it names `@<slug>` as a whole handle, in any case, and is not written by the bot itself. A comment read that fails is a failed search: nothing from that read is recorded and the window stays.
- **The entry.** Kind `mention`, shown under All and GitHub: "@<author> mentioned @<slug> on <owner>/<repo>#<n>: <first line of the comment, trimmed>", a link to the comment, unread until opened, one entry per comment or review id (a new mention in another comment is a new entry; an edit updates the same entry and keeps its read flag), removed on Disconnect with the other GitHub entries. Its one action, Review it (`review-pull-request`), runs in the window: it asks the chat "Review the pull request <url>" and opens the chat, so the existing code-review flow prepares its cards; posting stays the approved write ADR 0043 and ADR 0032 already make it. The entry and the poll write nothing on GitHub.
- **Declared.** The GitHub feature's `Notifications` row (Read) names these reads: the mention search with the reads of the description, comments, review comments and reviews of each pull request found, and the read of the bot App's own name and handle ([[pages/decisions/0044-scout-acts-on-github-as-its-own-app]]).

## Amendment v1.5 (2026-11-11): Pause, Continue and Stop on a running job

The owner, translated: "we should have a way to pause the audit", and the item that is running "could have a stop icon on the right". The Decision's "the panel never runs an action by itself (Continue the audit asks through the chat)" changes in its example only: the panel still runs nothing by itself, and Continue is now also a button on a paused audit's entry. A running audit's entry offers Pause and Stop at the right of its title, a paused one Continue and Stop, and a running test run the local chat started Stop; the test run gets an entry of its own, opened by the local turn's progress line and closed by the end of the last turn that runs that pull request's tests, or by its stop notice.

- **Only a click in the desktop.** The renderer sends the entry's id and the action; main checks that it holds that entry, that the entry offers that control and that no other click on it is unanswered, takes the job from the entry's own id, and calls the control plane's `ControlJob`, which only the desktop's token reaches. The write is the control plane's and is already declared ([[pages/decisions/0032-per-feature-write-permissions]]); every click ends in the write log.
- **Never a state the worker has not reported.** While main asks, the buttons stay and are inactive; a queued click replaces them with what Scout does ("Pausing after this area", "Stopping after this area", "Continuing", "Stopping") until the worker's notice changes the entry or its record in the write log says the click changed nothing; a refusal, a control plane that does not answer, no record within 150 seconds, and no notice within 11 minutes of a record that says applied each show their sentence on the entry and bring the buttons back. A Stop the AI Source's pause overtook still ends the paused audit as stopped, from its record.
- **The Writes history** colours an owner's click by its control status, never by its text.

How it works, its citations and its tests: [[pages/services/desktop-ui]], Pause, Continue and Stop on a running job.

## Amendment v1.6 (2026-11-12): when a job entry is unread, and a reopened audit

The owner settled both on 2026-11-12, after an audit of the bell's job entry against the artboard. Two rules of the Decision's store change; the rest stands.

- **Unread again only on a state change.** An entry recorded again with its id was unread again on every update, so each area an audit finished made its entry unread and raised the badge. A job entry the owner has read (an audit, a test run) now stays read while it keeps its kind; it turns unread again when its state changes (paused, stopped, finished, failed) or when a note comes, the sentence that says a click of his needs him again. What answers his own click (the pause he asked for, the stop he asked for) stays read, as the artboard draws it. The badge follows. The time beside a running job stays the time since its last activity.
- **A reopened audit is in progress.** An entry saved as running came back as failed with "Scout closed before this finished." The audit worker resumes a job Scout closed in the middle on its next start ([[pages/services/audit]]), so a running audit now comes back as it was saved, in progress with its last "Area n of m done"; only an entry that cannot resume, a test run among them, comes back as failed.

How it works, its citations and its tests: [[pages/services/desktop-ui]], Notification center.

## Consequences

- **Better:** the owner sees background work and GitHub activity in one place; the theme follows the system or his choice.
- **Worse:** one more read every five minutes while GitHub is connected; bounded by GitHub's poll interval and a conditional request.
- **Neutral:** screens keep their current look until their own migration replaces their literals.

## Enforcement

- `ui/packages/main/test`: the theme choice stored, defaulted to System and pushed to every window; the notification store's cap, order, persistence and read flags; each source records its entry; the GitHub poll's interval, conditional request, reason filter, missing permission (403) and Rule I failures (timeout, 401, 5xx) leave the store intact and say so once.
- `ui/packages/renderer`: the top bar, the theme switch's three states, the bell's badge and panel, filters and Mark all as read, in a matrix spec.
- v1.3: `ui/packages/main/test/notifications-worker-health.test.ts` (the transitions, table-driven), `ui/packages/main/test/notifications-approvals.test.ts` (the entry, its replacement and removal, the queue that cannot be read), the disconnect cases in `ui/packages/main/test/github-activity.test.ts`, and the error-only case in `ui/packages/renderer/src/app/core/notice.service.spec.ts`.
- v1.4: the mention cases in `ui/packages/main/test/github-activity.test.ts` (the query, the confirmation, the whole-handle table, the bot's own comment, one entry per comment, Disconnect, the budget with the extra search, a failing search and a failing comment read) and the mention entry and Review it in `ui/packages/renderer/src/app/shell/notification-bell.component.spec.ts`.
- v1.5: `ui/packages/main/test/notifications-job-controls.test.ts` (every control on every entry against every answer, the record and the notices after a click, the two bounds, a Stop the AI Source's pause overtook, the test run's entry with two runs of one pull request, the channel from every sender and the forged requests), `ui/packages/renderer/src/app/shell/notification-bell.component.spec.ts` (the controls drawn, inactive, replaced, the focus, the notes) and `ui/e2e/tests/bell-job-controls.spec.ts` through the real control plane.
- `tests/Scout.ControlPlane.Tests`: the GitHub catalog declares the Notifications read row.

## Related

- [[pages/services/desktop-ui]], [[pages/services/desktop-github]], [[pages/decisions/0041-angular-material-in-the-renderer]], [[pages/decisions/0032-per-feature-write-permissions]]
