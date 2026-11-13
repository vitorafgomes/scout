---
title: "YouTube creator assistant"
version: "1.0"
date: 2026-11-02
changes: "Initial initiative page: survey of claude-youtube and the plan for a read-only YouTube plugin with browser sign-in"
page_type: roadmap
status: active
description: "Initiative: give Scout what the claude-youtube skill offers (channel audit, analytics, competitor research, SEO, scripts) as a read-only plugin over the YouTube APIs with browser sign-in, grounded in live data and dated benchmarks."
source:
  - chat
  - "https://github.com/AgriciDaniel/claude-youtube (HEAD 84c3fa6, 2026-04-10, read in full on 2026-11-02)"
reliability: medium
---

# YouTube creator assistant

> A creator asks Scout, in Discord, Slack or the desktop quick-ask window, how the channel is doing, what to make next, how to title it and why a video dropped viewers, and gets an answer grounded in the channel's own numbers, with sources and dates. Scout reads YouTube; it never posts, uploads, edits or schedules anything there.

## Why now

The owner asked on 2026-11-02 whether Scout could offer what [claude-youtube](https://github.com/AgriciDaniel/claude-youtube) offers, and do it better, with browser-based authentication. That skill is a Claude Code plugin: 5,328 lines of markdown instructions (one orchestrator, 14 sub-skills, 9 reference guides, 9 channel templates) and 1,307 lines of Python that read the YouTube Data API v3, the YouTube Analytics API v2 and transcripts through `yt-dlp`. Everything it does is analysis or text generation; nothing in it writes to YouTube, so the whole surface fits Scout's read-only contract ([[pages/invariants/no-executive-writes]], [[pages/decisions/0009-descriptive-vs-executive-writes]]). Scout already has the two pieces the skill lacks: a desktop that signs the owner in through the system browser and keeps tokens in the OS keychain ([[pages/decisions/0018-chatgpt-plan-sign-in]], [[pages/decisions/0019-github-app-device-flow]]), and a worker that turns read functions into chat answers through Semantic Kernel ([[pages/services/skills]], [[pages/services/plugin-github]]).

## What claude-youtube is (survey, 2026-11-02)

Read in full from a clone kept outside the repository. Nothing was run.

| Layer | What it holds | How it is used |
|---|---|---|
| `SKILL.md` (237 lines) | Routing of 14 `/youtube <command>` triggers; asks niche, size tier and goal first (audit, analyze and competitor also ask the channel handle); picks one of 9 channel-type templates by letting the model classify the niche; loads reference guides on demand; "quality gates" (specificity, data grounding, completeness) with one regenerate-and-retry loop; optional MCP servers DataForSEO (paid keyword and SERP data) and NanoBanana (Gemini image generation for thumbnails). | Orchestrator prompt. |
| 14 sub-skills (2,448 lines) | audit, analyze, competitor, seo, ideate, calendar, hook, script, thumbnail, strategy, shorts, repurpose, monetize, metadata. Each lists its inputs (user, scripts, DataForSEO or WebSearch), the references it loads and a fixed output shape (A-F scorecards, 3 title variants, 10 idea cards, a month grid, a 150-words-per-minute script with pattern interrupts, a 7-stream revenue table). | Prompt per intent. None performs an action on YouTube; "publish" and "schedule" content is advice. `thumbnail` is the only one that produces files (images, when NanoBanana is configured). |
| 9 references (1,712 lines) | Algorithm mechanics, analytics funnel ratios and RPM/CPM by niche, SEO rules and limits, retention scripting, thumbnail CTR by niche, Shorts, monetization tiers and rates, repurposing, the DataForSEO tool catalogue. | The "data grounding" the quality gate checks against. |
| 9 templates (931 lines) | Per channel type (education, entertainment, tutorial, vlog, review, commentary, niche authority, personal brand, Shorts-first): traffic mix, RPM and CTR targets, Hub/Hero/Help mix, cadence, 10 title patterns, thumbnail formula, hook style, monetization order, growth milestones, 3 failure modes. | Loaded once per channel. |
| 6 Python scripts (1,307 lines) | `fetch_channel_data` (channels, playlistItems, videos lists; API key), `search_competitor_videos` (search then videos; API key), `fetch_video_analytics` (Analytics `reports.query` by video, traffic source and day; OAuth), `fetch_transcript` (`yt-dlp` auto-subtitles, no API), `youtube_auth` (API key from env or `~/.claude/youtube-credentials.json`; OAuth through `InstalledAppFlow.run_local_server`, scopes `youtube.readonly` and `yt-analytics.readonly`, token stored in plain text under `~/.claude/.tmp/`), `quota_tracker` (local 10,000 units per day counter). | Called by the orchestrator when a key or token exists; otherwise the user pastes Studio data. |

### What it does well

- Covers the whole creator workflow in one place, from "what is wrong with my channel" to a copy-paste upload package.
- Asks for context first (niche, size, goal) and shapes every answer to it.
- Keeps every benchmark in a file the model must cite, instead of letting it improvise numbers.
- Works with no credentials at all, by accepting pasted data.

### Where it falls short (evidence from the files)

- **Scripts barely wired in.** `fetch_transcript.py` and `fetch_video_analytics.py` are referenced by no sub-skill: `analyze` asks for Studio screenshots, `repurpose` asks for a pasted script. There is no comments reader although the quota table prices `commentThreads.list`.
- **Quota accounting is wrong.** `videos.list` is charged one unit per id instead of one per call; the "Pacific day" ignores daylight saving; the counter only knows what these scripts report; transcripts are said to cost 1 to 2 units and cost none.
- **Stale and contradictory benchmarks.** Figures are tagged `[2025]` with no per-figure date; the RPM table is duplicated across two guides and already disagrees (tech CPM 8 to 14 dollars in one, 4 to 8 in the other); size tiers are 4 in the orchestrator, 6 in `audit`, 5 in `hook`; title length, hashtag count, pattern-interrupt spacing and CTA timing each have two or three incompatible rules.
- **Credentials in plain text.** The OAuth refresh token and the API key sit in `~/.claude` with default permissions, in a cache folder; the browser flow needs a desktop and cannot run on a server.
- **Transcripts by scraping.** `yt-dlp` pulls auto-subtitles from the YouTube site, which the YouTube Terms of Service do not permit for third-party videos.
- **No tests, no dependency pins, no CI** for the scripts; API exceptions are mostly uncaught.
- **Hard to carry into a chat bot as is:** four-agent fan-out, a self-correction loop, screenshot ingestion and very long templated outputs.

### Also surveyed and set aside (2026-11-02)

- **ImprovedTube** (`code-charity/youtube`): a browser extension that restyles the YouTube site for viewers (player defaults, hidden elements, themes, hotkeys). No API, no data access; a different product. Nothing to reuse.
- **youtube-dl** (`ytdl-org/youtube-dl`; the active fork is yt-dlp): downloads media and metadata by scraping site pages with per-site extractors. For Scout it adds only third-party captions and the media file, both by scraping, against YouTube's terms and outside Scout's purpose; metadata already comes from the Data API, the owner's own captions come through the API in phase 3. A C# port (or the YoutubeExplode library) would change the language, not the problem: an extractor that breaks whenever the player changes and a terms-of-service risk. If third-party captions are ever wanted, they go in a separate opt-in plugin with its own ADR; not on this plan.

## Scope

- **In scope:** a `Scout.Plugin.YouTube` project of read functions over the YouTube Data API v3 and the YouTube Analytics API v2; a YouTube card in the desktop with browser sign-in (OAuth 2.0 with PKCE through the system browser and a loopback redirect) and a pasted-API-key alternative for public data; token custody in the desktop's keychain vault with a token file for the worker, as GitHub does today; the creator intents (audit, analyze, competitor, seo, ideate, metadata, hook, script, thumbnail brief, strategy, calendar, shorts, repurpose, monetize) as Scout answers grounded in the plugin's data and in one dated, sourced benchmarks page; captions of the owner's own videos through the Data API; the same answers in Discord, Slack and the quick-ask window.
- **Out of scope:** any write to YouTube (comments, uploads, playlist edits, scheduling); image generation (NanoBanana) and paid keyword data (DataForSEO) in the first release; transcripts of videos the owner does not own (no scraping); screenshot reading; a Teams-specific path.
- **Anti-goals:** a token or key in a file the renderer or the chat can reach; a benchmark in an answer without its source and date; a function the model can call that reaches a write.

## Target outcome

By the end of phase 2, the owner connects a YouTube channel from the desktop in one browser round trip, asks "how did my channel do in the last 28 days and which video lost viewers fastest" in Discord, and receives views, watch time, subscribers, traffic sources and the retention proxy per video, with the day range and the data lag stated, and no number the API did not return.

## Status

- **As of 2026-11-02:** phase 1 worker side built: `Scout.Plugin.YouTube` with `youtube_channel_overview`, `youtube_recent_videos` and `youtube_video_stats` over the Data API v3 with plain GETs (`src/Scout.Plugin.YouTube/YouTubePlugin.cs:32`, `:54`, `:90`), the token file or an API key, self-gating when neither is set (`src/Scout.Plugin.YouTube/YouTubeServiceCollectionExtensions.cs:45`), the quota, 401 and 403 short answers (`src/Scout.Plugin.YouTube/YouTubeClient.cs:156`), registered by the host (`src/Scout.Host.Console/HostComposition.cs:88`) and listed as the `youtube` feature (`src/Scout.ControlPlane/Features/FeatureCatalog.cs:108`). Tests: `tests/Scout.Plugin.YouTube.Tests/` (GET only and key in the query string only: `tests/Scout.Plugin.YouTube.Tests/YouTubeTokenFileHandlerTests.cs:133`; the key in no message or log: `tests/Scout.Plugin.YouTube.Tests/YouTubeClientTests.cs:325`). Detail on [[pages/services/plugin-youtube]].
- **Desktop side built (2026-11-02):** the YouTube card on the Features screen with Sign in with Google (loopback listener on `127.0.0.1` from port 8765, PKCE S256, `state`, offline access, the two read scopes) and Use an API key (checked with one public GET); `GoogleAuth` (`ui/packages/main/src/oauth/google-auth.ts:103`) keeps the session under `youtube.session` or the key under `youtube.api-key`, refreshes on a serial queue, treats `invalid_grant` as signed out, revokes on Disconnect, and writes `<userData>/youtube-token.json` (`{access_token, expires_at}` in unix ms, mode 0600, through a rename); the worker gets `SCOUT__PLUGINS__YOUTUBE__TOKENFILE` or `SCOUT__PLUGINS__YOUTUBE__APIKEY` and the allow-list gate (`ui/packages/main/src/worker/build-env.ts:94`). The OAuth client comes from `SCOUT_GOOGLE_CLIENT_ID` and `SCOUT_GOOGLE_CLIENT_SECRET` or `<userData>/google-oauth-client.json`; without it the card explains where the file goes. Tests: `ui/packages/main/test/oauth-google.test.ts`, `ui/packages/main/test/oauth-google-key.test.ts`, `ui/packages/main/test/ipc-youtube.test.ts`, `ui/packages/renderer/src/app/features/youtube-connect.component.spec.ts`. Detail on [[pages/services/desktop-ui]] (YouTube connection).
- **Next milestone:** phase 1 spike, after the owner registers the Google OAuth client.
- **Blocker:** a Google Cloud project with the YouTube Data API and YouTube Analytics API enabled and an OAuth client of type Desktop; only the owner can create it. Its client id is a public identifier; Google issues a "client secret" for desktop clients too and documents it as not confidential, but Scout treats it as configuration all the same (Rule G): read from configuration and environment, never committed.

## Design

### Authentication, in the desktop, through the browser

- The YouTube card in AI Sources (or Features) offers **Sign in with Google** and **Use an API key**. Sign-in follows ADR 0018's shape: the main process opens the system browser at Google's authorization endpoint with PKCE and a loopback redirect on `127.0.0.1`, exchanges the code, and stores `{access_token, refresh_token, expires_at, channel_id, channel_title}` under a main-only vault key `youtube.session`; the renderer gets status only (signed in, channel title). Scopes: `https://www.googleapis.com/auth/youtube.readonly` and `https://www.googleapis.com/auth/yt-analytics.readonly`. The monetary scope `yt-analytics-monetary.readonly` is a separate, explicit opt-in on the card, off by default.
- Main refreshes the token the way it refreshes GitHub's (one serial queue, on a timer and on wake) and writes `{access_token, expires_at}` with mode 0600 to `<userData>/youtube-token.json`; the worker receives `SCOUT__PLUGINS__YOUTUBE__TOKENFILE` and is restarted on connect and disconnect, exactly as [[pages/decisions/0019-github-app-device-flow]] describes for GitHub.
- A pasted API key unlocks public data only (channels, videos, search, comments, caption lists). It is stored under `youtube.apiKey` in the vault and passed to the worker through the same file. Google rate-limits keys, not users, so the card shows the day's estimated quota use.
- Consequence to plan for: `youtube.readonly` is a sensitive scope. While the Google app is in "Testing" it accepts up to 100 listed test users; publishing it to everyone requires Google's verification. For the owner's own use, Testing is enough.

### The plugin: read functions only

`Scout.Plugin.YouTube`, same shape as [[pages/services/plugin-github]]: an `IYouTubeClient` abstraction with one implementation over plain `HttpClient` against the Data API v3 (and, in phase 2, the Analytics API v2), no Google SDK ([[pages/decisions/0030-youtube-plugin-read-only-with-browser-sign-in]]), GET and `reports.query` only, 30 second timeout, failures summarised without quoting Google's body or the request URL. Functions the model can call, all descriptive:

| Function | Reads | Needs | Quota units |
|---|---|---|---|
| `youtube_channel_overview` | `channels.list(mine)` or by handle: title, subscribers, views, video count, uploads playlist | key or sign-in | 1 |
| `youtube_recent_videos` | `playlistItems.list` + `videos.list` in one batch of up to 50: title, published, duration, views, likes, comments | key or sign-in | 2 |
| `youtube_video_stats` | `videos.list` for up to 50 ids | key or sign-in | 1 |
| `youtube_search_videos` | `search.list` by query, optional channel, order and date window, then `videos.list` | key or sign-in | 101 |
| `youtube_video_comments` | `commentThreads.list`, newest or top, capped at 100 | key or sign-in | 1 per page |
| `youtube_my_analytics` | Analytics `reports.query` for `channel==MINE`: views, estimatedMinutesWatched, averageViewDuration, averageViewPercentage, subscribersGained and Lost, by day and by `insightTrafficSourceType`, over 7, 28 or 90 days ending two days ago | sign-in | none (Analytics API has its own, generous quota) |
| `youtube_my_video_analytics` | the same per video, up to 10 ids, plus `cardClickRate` and `annotationClickThroughRate` where available | sign-in | none |
| `youtube_my_captions` | `captions.list` and `captions.download` for the owner's own videos, text only | sign-in | 50 per download |

Quota is tracked from the API itself: every `quotaExceeded` error becomes a short answer ("YouTube's daily quota for this key is spent; it resets at midnight Pacific time") and the plugin keeps a local estimate with Google's published costs (search 100, lists 1 per call, caption download 50) for the desktop card. `search` is the expensive call, so the competitor intent is capped at three searches per answer.

Impressions and click-through rate are not in the Analytics API v2; they live in the Reporting API's bulk reports, which are produced daily and need a job to be created first. They stay out of the first release and are named in the answers as "not available through the API; read it in Studio".

### The intents: Scout answers, not slash commands

The 14 sub-skills become intents the conversation layer recognises in natural language ("audit my channel", "why did this video lose viewers", "titles for a video about X"), the way the GitHub and Kubernetes plugins already answer questions. The model gets three things per answer: the live data from the functions above, the channel's own history as context, and one benchmarks page.

- **Benchmarks page** (`pages/domain/youtube-benchmarks`, to be written in phase 2): every figure carries `as of`, `source` and `applies to` (niche, size tier). A single size-tier scale (new under 1,000; growing 1,000 to 10,000; established 10,000 to 100,000; authority above) and one rule per topic end the contradictions listed above. The page is reviewed on a date, not "when someone notices".
- **Channel type:** one table replaces the nine templates; the model picks the row from the channel's uploads and description, and says which row it picked.
- **Long answers** (calendar, ten ideas, a full script) go to the thread the Discord adapter already opens and are split across messages ([[pages/decisions/0024-voice-messages-transcribed-locally]], same change); in the quick-ask window they render in full.
- **No multi-agent fan-out.** The audit intent calls the functions it needs in one conversation; the four "dimensions" are sections of one answer.

### What makes it better than the original

1. Live data instead of pasted screenshots: analytics, comments and the owner's own captions through official APIs, with the owner signed in once from the desktop.
2. Tokens in the OS keychain, a 0600 token file for the worker, nothing under a cache folder; no scraping.
3. One benchmark source with dates and sources; one size scale; contradictions removed by construction.
4. Correct quota accounting from the API's own errors; `search` capped.
5. Answers where the team already talks, in the user's language, from Discord, Slack or the desktop, and in a thread when long.
6. Tests: one xUnit project with Shouldly, a failing-call test per Google endpoint (Rule I), and the review gate's `read-only` lens on every function.

## Phases

### Phase 1: sign-in and the channel at a glance
- **Owner:** owner (Google OAuth client registration), Claude (code)
- **Target:** first owner test one week after the client exists
- **Status:** not started
- **Scope:** `Scout.Plugin.YouTube` with `youtube_channel_overview`, `youtube_recent_videos`, `youtube_video_stats`; the desktop card with Sign in with Google (ADR 0018 shape) and Use an API key; token file to the worker; Discord, Slack and quick-ask answer "how is my channel doing".
- **Success criteria:** the owner signs in from the desktop in one browser round trip; "how is my channel doing" returns the channel numbers and the five latest videos; the token never appears in a log or a renderer channel (test); a 401 and a 403 from Google produce the short failure text (tests).

### Phase 2: analytics and the audit
- **Target:** two weeks after phase 1
- **Scope:** `youtube_my_analytics`, `youtube_my_video_analytics`, the benchmarks page, the `analyze` and `audit` intents, optional monetary scope.
- **Success criteria:** the target outcome above; every benchmark in an answer carries its date and source; the data lag (two days) is stated.

### Phase 3: research
- **Target:** two weeks after phase 2
- **Scope:** `youtube_search_videos`, `youtube_video_comments`, `youtube_my_captions`; the `competitor`, `ideate`, `seo` and `metadata` intents; search cap and quota messages.
- **Success criteria:** "what are channels like mine doing that I am not" answers from three searches at most and names the gap from real comments; `seo` returns three titles within YouTube's limits, a description, tags under 500 characters and chapters, grounded in the top results for the query.

### Phase 4: the creative playbooks
- **Target:** two weeks after phase 3
- **Scope:** `hook`, `script`, `thumbnail` (text brief only), `strategy`, `calendar`, `shorts`, `repurpose`, `monetize` as intents over the same data and benchmarks; the channel-type table.
- **Success criteria:** a script answer states its length target, hook, mid-roll CTA position and interrupt spacing from the benchmarks page; a calendar answer is a table the Discord adapter posts in a thread.

### Phase 5 (optional, each its own ADR)
- Keyword and SERP data behind a provider abstraction (DataForSEO or another), paid by the owner, opt-in.
- Thumbnail image generation as an explicit, opt-in generator that writes only to the owner's machine.
- Reporting API bulk reports for impressions and click-through rate.

## Decisions pending

- `pages/decisions/0030-youtube-plugin-read-only-with-browser-sign-in` (to be drafted at phase 1): the plugin's boundary, the functions, the scopes, token custody, quota handling.
- **Owner:** create the Google Cloud project and the Desktop OAuth client; decide whether the monetary scope is wanted at all; confirm that image generation and paid keyword data stay out of the first release; say which chat client to test first.

## Services affected

- [[pages/services/desktop-ui]]: the YouTube card, sign-in, token file, quota display.
- [[pages/services/skills]] and [[pages/services/conversation]]: the new plugin's functions and the long-answer threading.
- [[pages/services/host-console]]: plugin registration and the token-file environment variable.
- New: `pages/services/plugin-youtube` after phase 1 ships.

## Risks

- Google app verification for the sensitive scopes if the plugin is ever offered beyond the owner's test users.
- The Data API's 10,000 units per day: one competitor answer can spend 300; the cap and the quota message exist for that.
- Analytics lag of about two days; answers say so.
- Captions of other people's videos are not reachable without scraping; the plan does not offer them.
