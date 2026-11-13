---
title: "Desktop GitHub card - history"
version: "1.1"
date: 2026-11-08
changes: "2026-11-11 entry: Use a token opens the credential dialog with the token only. Earlier: initial history note, the mention of the Scout bot in the bell and the slug that follows a rename"
page_type: reference
status: artefact
description: "What changed in the desktop's GitHub card, the Scout bot and the bell's GitHub poll, and when."
source:
  - chat
  - ui/packages/main/src/oauth/github-bot.ts
  - ui/packages/main/src/notifications/github-activity.ts
reliability: high
---

# Desktop GitHub card - history

> Dated changes of [[pages/services/desktop-github]]; the service page states how it works now.

## 2026-11-11 - Use a token opens the credential dialog, with the token only

- **Before:** "Use a token" opened an inline password field under the Connect row, with Save, a help line and a "Create a fine-grained token" link; a refused token showed main's sentence under the card. The Features artboard drew a credential dialog with a required "Default repository URL" and an optional token, so the prototype migration listed it as owed.
- **Owner's decision:** the dialog has the token only. The "Default repository URL" field left the artboard because nothing in Scout reads such a setting; a real default repository would be a product item of its own ([[pages/roadmap/followups]]).
- **Changed:** the artboard's GitHub dialog has one required "Personal access token *", the steps for making a fine-grained token with read access to Metadata, Contents, Checks, Commit statuses, Pull requests and Issues, and the previews for an empty Save, saving and GitHub refusing the token. The card opens that dialog (`sc-github-token-dialog`) in place of the inline field. Empty saves nothing; a refusal keeps it open with main's sentence and the field emptied; a kept token closes it with "GitHub credentials saved". The shared `sc-credential-dialog` was not reused, because the Features screen closes it before its save answers; the new dialog takes its look (`DIALOG_STYLES`). Main's token check (`GET /user`) and its storage did not change.

## 2026-11-11 - "installed on <n> repositories" after Connected as

- **Asked for:** the Features artboard draws "Connected as **acme-owner** · installed on 6 repositories" in the GitHub panel, and the prototype migration listed it as owed: main's GitHub status carried no count of the App's repositories ([[pages/roadmap/prototype-migration]]).
- **Changed:** with the browser session, the status read that already lists the App's installations also reads `GET /user/installations/{id}/repositories?per_page=1` for each listed installation and sums `total_count`; the status carries it as `repositories`, and the line after the login reads " · installed on <n> repositories", "1 repository" at one, nothing at 0 (Install shows instead), with a pasted token or before a count was read. A failed read keeps the last count and logs its error code once per failure streak, since the card polls every 3 seconds in some states. The artboard gained the 1 and 0 previews, the 0 one with the Install button. ADR 0019 amended the same day.

## 2026-11-08 - a mention of the Scout bot reaches the bell

- **Asked for (translated):** "the notifications must show that the bot was mentioned somewhere"; a team asks Scout for a review by mentioning the bot by its name in a pull request, with no label and no workflow change, and every owner names their own bot, so Scout watches the owner's bot and follows a rename ([[pages/roadmap/future-improvements]], F21).
- **Found first, on the live API:** `mentions:<app-slug>` does not find an App's bot (a 422 or nothing); the phrase search `"@<slug>" in:comments,body is:pr` does, tokenised, so every hit needs confirming. Scout's App is private, so `GET /apps/<slug>` and `GET /users/<slug>` answer 404 to the owner's token: the kept slug, re-read with the App's JSON Web Token, is the only source.
- **Changed:** the slug is read again from `GET /app` at start and on each bot status; the bell's poll adds one phrase search inside its 20-a-minute budget, confirms each hit from the pull request's body and its comments, and records one `mention` entry per comment with Review it, which asks the chat to review that pull request; the card shows "Ask for a review with @<slug>" with Copy. ADR 0045 v1.4, ADR 0044 amended 2026-11-08.
- **Then, the same day:** the owner renamed the bot on GitHub and the desktop still showed the old name. `GET /app` now also gives the display name, kept with the slug; the card shows the name and the handle, "Change the name on GitHub" beside "Add a picture on GitHub" (GitHub has no API to rename an App, so Scout opens its settings page) and Refresh, and says when the last read failed.
- **Then:** review summaries are read too (`/pulls/<n>/reviews`, a review counted when submitted in the window, one entry per review id), and the catalog's `Notifications` row names every read of this lane: the mention search with its comment, review comment and review reads, and `GET /app` for the bot's name and handle.

## Version notes: `pages/services/desktop-github.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- v1.16 (2026-11-12): the bot lists every repository of its installation, page after page up to 50 pages, and keeps the last full list when a later page fails; an OAuth wait whose signal is already aborted is cancelled at once with no listener opened.
- v1.15 (2026-11-12): the Notifications row shows its one sentence, drawn on the artboard too.
- v1.14 (2026-11-12): the bot select and the Organization field are on while GitHub is not connected, only Create off, and the choice is kept when GitHub connects; the notices \"<login> was created on GitHub\", \"<login> is installed on <n> repositories\" on a change the card saw, and \"Scout no longer uses the bot\" after Remove; citations re-pointed by content.
- v1.13 (2026-11-11): Use a token opens the artboard's credential dialog with the token only: empty saves nothing, a refusal keeps it open with main's sentence, a kept token closes it with GitHub credentials saved; the Default repository URL field left the artboard; citations re-pointed by content (2026-11-11). Earlier: v1.12 (2026-11-11) \"Connected as <login>\" says how many repositories the Scout GitHub App is installed on: main reads each installation's repository count (GET /user/installations/{id}/repositories?per_page=1) with the owner's browser session and sums them; citations re-pointed by content.
- v1.11 (2026-11-10) the verdict channel's sender rule and Post as the verdict comment stated.
- v1.10 (2026-11-08) the bot's name and slug are read again from the App (GET /app) at start and whenever the card reads the bot status, so a rename is followed; the card shows the bot's name and handle, Change the name on GitHub and Refresh, and Ask for a review with @<slug> with Copy; the bell's poll adds one search for a mention of the bot, confirms each hit from the pull request's body and comments, and records one mention entry per comment or review with Review it; the catalog's Notifications row names the mention reads and GET /app; citations re-pointed by content.
- v1.9 (2026-11-08) Add a picture on GitHub: once the bot exists its section opens the App's settings page from the bot status, user or organization, through the existing openExternal allow-list; the GitHub connect section moved into the Features side panel and the card sums it up; the bell's GitHub activity reads every page at no more than 20 searches a minute, going on from the next page on the following tick, keeps comment counts, resumes after a token refresh, never uses an expired token and leaves the bell on Disconnect.
- v1.8 (2026-11-08) a GitHub credential change or loss revokes the github write grant, a revoke the control plane missed is kept and sent again and pushes features.changed, which the card re-reads; a refused bot switch goes back; a bot status that cannot be read keeps the bot section with its reason; citations re-pointed by content.
- v1.7 (2026-11-07) The Scout bot section shows while GitHub is not connected, disabled with Connect GitHub first to create the bot.; the bot's repositories fold behind Repositories: N (<login> can post in these); citations re-pointed by content.
- v1.6 (2026-11-07) Disconnect and Remove the bot ask first through the shared confirmation; citations re-pointed by content after the Features screen moved to the theme.
- v1.5 (2026-11-07) the bell reads GitHub activity through the search API (ADR 0045 v1.1), replacing the notifications poll.
- v1.4 (2026-11-07) the GitHub notifications poll for the bell (ADR 0045) and GitHubAuth.accessToken.
- v1.3 (2026-11-07) the card re-reads the bot's repositories when it loads.
- v1.2 (2026-11-07) BOTREPOS, and a restart when the installation's repositories change.
- v1.1 (2026-11-07) one verdict action per pull request and commit, the status carries its key, and the lookup for a later window.
- v1.0: split from desktop-ui: the GitHub sign-in, the pasted token, the optional Scout bot, and the verdict the review card gives through the control plane
