---
title: "ADR 0030 - YouTube plugin: read-only over the official APIs, signed in from the desktop through the browser"
version: "1.1"
date: 2026-11-02
updated: 2026-11-02
changes: "Amended: a default Desktop OAuth client ships in the code; env and file override it"
page_type: adr
status: accepted
description: "Scout reads YouTube through the Data API v3 (and, in a later phase, the Analytics API v2) with plain HTTP GETs; the owner signs in from the desktop through the system browser with OAuth 2.0 and PKCE, or pastes an API key for public data; tokens live in the desktop keychain vault and reach the worker through a 0600 token file; Scout never posts, uploads, edits or schedules anything on YouTube."
supersedes: ""
superseded_by: ""
source:
  - chat
  - "vault/pages/roadmap/youtube-creator-assistant.md"
  - "src/Scout.Plugin.YouTube/"
  - "ui/packages/main/src/oauth/google-auth.ts"
reliability: high
---

# ADR 0030 - YouTube plugin: read-only over the official APIs, signed in from the desktop through the browser

> Scout answers questions about a YouTube channel by reading the official YouTube APIs with GET requests only, authenticated either by the owner's browser sign-in held in the desktop's keychain or by a pasted API key for public data; it never writes to YouTube.

## Status

- [ ] Proposed
- [x] Accepted
- [ ] Deprecated / superseded by

Updated: 2026-11-02. Accepted on the owner's instruction after approving the plan page: start phase 1 of [[pages/roadmap/youtube-creator-assistant]].

## Context

The owner asked whether Scout could match or beat the claude-youtube Claude Code skill, a creator assistant built from prompt files and Python scripts over the YouTube Data API, the Analytics API and transcripts scraped with yt-dlp, and asked for browser-based authentication. The survey on [[pages/roadmap/youtube-creator-assistant]] found that everything the skill does is analysis or text generation, so the whole surface fits Scout's read-only contract ([[pages/invariants/no-executive-writes]], [[pages/decisions/0009-descriptive-vs-executive-writes]]), and that its weak points were the credential handling (plain-text tokens under a cache folder), the scraping and the stale, contradictory benchmarks.

Scout already has the two patterns a plugin needs: the GitHub plugin ([[pages/services/plugin-github]]) for a read-only HTTP plugin with a token file written by the desktop ([[pages/decisions/0019-github-app-device-flow]]), and the ChatGPT sign-in for a browser flow with a loopback redirect and PKCE ([[pages/decisions/0018-chatgpt-plan-sign-in]]). Google's OAuth for installed applications needs an authorization code with PKCE, a loopback redirect on `127.0.0.1`, and the client's id and secret on the code exchange; Google documents the secret of a Desktop client as not confidential, but Scout treats it as configuration all the same (Rule G).

## Decision

- **A plain HTTP plugin, no SDK.** `Scout.Plugin.YouTube` ([[pages/services/plugin-youtube]]) follows the GitHub plugin's shape: an `IYouTubeClient` over `HttpClient` against `https://www.googleapis.com/youtube/v3/`, GET only, 30 second timeout, one place that turns a 401, a 403 (quota or permission), a 404, a 400 and a 5xx into a short sentence that never carries the key or the token; Semantic Kernel functions `youtube_channel_overview`, `youtube_recent_videos` and `youtube_video_stats` in phase 1, each returning plain lines the model composes from. No vendor type leaves the plugin ([[pages/invariants/pluggable-boundaries]]).
- **Two credentials, one at a time.** A browser sign-in (scopes `youtube.readonly` and `yt-analytics.readonly`, the second for phase 2) or a pasted Data API key for public data. The desktop keeps the session under the main-only vault key `youtube.session` and the key under `youtube.api-key`; saving one clears the other, as the GitHub card does.
- **Sign-in in the desktop main process, through the system browser.** Authorization at `accounts.google.com` with PKCE S256, a `state`, `access_type=offline` and `prompt=consent`, the loopback listener started before the browser opens, the code exchanged at `oauth2.googleapis.com/token` with the client id and secret; the channel identity read once with `channels.list(mine)`. Main refreshes the token on a serial queue with a five minute margin and treats `invalid_grant` as the end of the session; sign-out revokes the token on a best-effort basis and removes the token file before the vault entry. The client id and secret come from `SCOUT_GOOGLE_CLIENT_ID` and `SCOUT_GOOGLE_CLIENT_SECRET`, else from `<userData>/google-oauth-client.json` (flat or Google's downloaded `installed` shape), else from the Desktop client Scout ships as constants in `ui/packages/main/src/oauth/google-auth.ts` (amended 2026-11-02 on the owner's decision: Google documents an installed application's client secret as not confidential, and the GitHub App's client id ships the same way; the shipped client belongs to the owner's Google Cloud project, so every sign-in through it counts against that project's quota and, while the project is in Testing, only its listed test users can sign in; a user with their own project uses the file or the environment). They are read when needed (sign-in, refresh and the status check), never logged, never copied to the vault or the renderer; when no source yields both values the card says sign-in is not configured and offers the key.
- **The worker reads a file, not the keychain.** Main writes `<userData>/youtube-token.json` as `{access_token, expires_at}` with mode 0600 through a temporary file and a rename, and starts the worker with `SCOUT__PLUGINS__YOUTUBE__TOKENFILE`; a pasted key travels as `SCOUT__PLUGINS__YOUTUBE__APIKEY`. The plugin re-reads the file when its modification time changes and sends only `GET`s. Either credential turns on the owner allow-list gate, as the GitHub credential does ([[pages/decisions/0021-allowed-users]]).
- **A control-plane feature like the others.** `youtube` joins the feature catalog with `WriteClass.None`, a scope matrix of read calls and setup steps; the desktop Features screen shows the connection card in place of the control-plane toggle, as for GitHub.
- **What never happens.** No call that is not a GET to `googleapis.com` other than the OAuth token and revoke POSTs; no comments, uploads, playlist edits or scheduling; no transcript scraping; no benchmark figure without a date and a source (phase 2's page); no paid keyword data or image generation in the first release.

## Consequences

- **Better:** live channel data in Discord, Slack and the quick-ask window, grounded in the official API; one browser round trip to connect; tokens in the OS keychain and a 0600 file, never in a cache folder; the model can only read.
- **Worse:** `youtube.readonly` is a sensitive scope: while the Google app is in Testing it accepts up to 100 listed test users, and offering the sign-in to everyone needs Google's verification; the Data API's 10,000 units per day is a hard ceiling (one search costs 100, so later phases cap searches); impressions and click-through rate are not in the Analytics API and stay "read it in Studio".
- **Neutral but noteworthy:** the key travels in the query string, as Google requires, so the client makes sure no exception message or log line carries a request URL.

## Alternatives considered

- **Google's .NET client libraries** (`Google.Apis.YouTube.v3`). Rejected for phase 1: three endpoints do not justify a dependency that brings its own auth stack, and plain `HttpClient` keeps the failing-call tests exactly like the GitHub plugin's.
- **The device flow** (as GitHub). Rejected: Google's device flow does not support the YouTube scopes for TV and limited-input devices in the way needed here, and the owner asked for the browser.
- **OAuth in the worker.** Rejected: the worker has no browser and no keychain; the desktop already owns both for ChatGPT and GitHub.
- **Transcripts through yt-dlp**, as the surveyed skill does. Rejected: scraping, against YouTube's terms; the owner's own captions come through `captions.download` in phase 3.

## Enforcement

- `tests/Scout.Plugin.YouTube.Tests`: every function sends only GETs; each failing call maps to its sentence; the key never appears in a message or a log; the token file handler re-reads on change and sends nothing without a file.
- `ui/packages/main/test/oauth-google.test.ts`: the loopback listener starts before the browser opens; PKCE S256 and state; the secret, the code, the tokens and the key never reach a log; only POSTs to Google's token and revoke endpoints; `invalid_grant` clears the session and the file.
- `ui/packages/shared/test/ipc-contract.test.ts`: the write-verb scan over the `scout:youtube.*` channels.
- The review gate's `read-only`, `secrets` and `boundaries` lenses on every change under `src/Scout.Plugin.YouTube/` and `ui/packages/main/src/oauth/google-auth.ts`.
