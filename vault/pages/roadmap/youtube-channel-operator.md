---
title: "YouTube channel operator"
version: "1.1"
date: 2026-11-02
changes: "One feature, and the pipeline in C#: the scheduler and every stage move to the worker; the desktop only shows and approves"
page_type: roadmap
status: active
description: "Initiative: Scout runs a YouTube channel end to end, from topic research and scripts through narration, visuals and an assembled video, to a review step, upload and scheduling the owner approves, and a learning loop over the channel's analytics; own C# code, official APIs, per-feature permissions (ADR 0032)."
source:
  - chat
  - "https://github.com/darkzOGx/youtube-automation-agent (README read on 2026-11-02)"
  - "vault/pages/roadmap/youtube-creator-assistant.md"
reliability: medium
---

# YouTube channel operator

> The owner gives Scout a channel mandate (audience, pillars, cadence, limits); Scout researches topics, writes the script, produces narration and visuals, assembles a real video, puts it in front of the owner for review, uploads and schedules it after approval, answers comments as drafts the owner approves, and learns from the channel's own numbers. Every write is declared, granted and approved as [[pages/decisions/0032-per-feature-write-permissions]] says.

## Why now

The owner tested the read-only YouTube plugin ([[pages/roadmap/youtube-creator-assistant]] phase 1, [[pages/decisions/0030-youtube-plugin-read-only-with-browser-sign-in]]), saw the channel in chat and said the goal is the whole production: "quero que seja um gerador de vídeos", pointing at `darkzOGx/youtube-automation-agent` as the reference: "quero ter algo igual a isso". That project (Node.js, MIT, one developer) does research, script, narration (ElevenLabs, OpenAI or Gemini), visuals (a free local slideshow by default, paid video models as an option), assembly with ffmpeg, a review gate, upload and scheduling through the YouTube Data API, Shorts repurposing, comment handling and a learning loop over 24-hour and 7-day analytics. The owner's constraints: our own code, in C#, no third-party scraping, security first; features with different permissions, granted one by one.

## Scope

- **In scope:** the stages below, inside the one `youtube` feature of the control-plane catalog with its declared rows and the owner's grant; the desktop as the only approval surface; official APIs only (YouTube Data API v3 with the upload and comment scopes, YouTube Analytics API v2, the connected LLM provider, a TTS and an image or video provider the owner connects); local rendering of the final video; Discord, Slack and the quick-ask window as the places to ask for a video and follow progress.
- **Out of scope:** scraping of any site; buying ads; anything the owner has not granted; writes outside Scout's own data folder; uploading without the per-video approval; third-party captions.
- **Anti-goals:** a video uploaded that nobody approved; a comment reply published that nobody read; a credential with write scopes held by a feature that does not need it; placeholder media reaching the review as if it were final.

## Target outcome

By the end of phase 5, the owner says "make a video about X" in Discord, reviews the script, the narration and the assembled video on the desktop's Production screen, approves it, and the video is uploaded as private or scheduled on the channel without the owner touching YouTube Studio; a day later Scout reports what the video did against the channel's own history.

## Status

- **As of 2026-11-02:** planned; nothing built. ADR 0032 accepted. Phase 1 of the read-only plan is in review and gives this initiative the sign-in, the token file and the catalog entry.
- **Next milestone:** ADR 0032's wording landed on the README, the site and the pipeline; then phase 1 below.
- **Blocker:** none for phases 1 and 2. Phase 3 needs a TTS and an image source the owner picks (see Decisions pending). Phase 5 needs the `youtube.upload` and `youtube.force-ssl` scopes on the sign-in, requested only once the write grant is given.

## Design

### One feature, every permission declared

The owner's rule (2026-11-02): "tem que ficar tudo dentro da mesma feature". The operator is the existing `youtube` catalog entry, not a set of features. Its contract lists every call with its access; the owner grants the write permission once on the YouTube card of the Features screen (the grant is a control-plane call, confirmed in a dialog that lists the write rows); every irreversible row still runs one action at a time after the owner approves it on the Review screen.

| Access | Rows of the `youtube` feature |
|---|---|
| read | `channels.list`, `search.list`, `videos.list`, `playlistItems.list`, `commentThreads.list`, analytics (YouTube Analytics API v2) |
| write | scripts, narration, images, scene list, the assembled video and thumbnails under Scout's data folder |
| write (irreversible), approved per action | `videos.insert` (upload), `videos.update` (metadata, visibility, publish time, a live title), `thumbnails.set`, `playlistItems.insert`, `comments.insert` (reply), `comments.setModerationStatus` |

`WriteClass.Executive`. Without the grant the feature is what phase 1 of the read-only plan shipped: reads only. The Google sign-in asks for the upload and comment scopes only when the grant is given (phase 5).

### The pipeline

1. **Mandate.** A Production screen in the desktop holds the channel mandate: objective, audience, content pillars, cadence, language, voice and style, blocked topics, budget caps for paid providers. Stored under Scout's own vault and data folder; editable only there.
2. **Research.** Topic candidates from the channel's history, the Data API's search and the connected model, each with an estimated fit to the mandate and the reasons; the owner picks or asks for more. No external write.
3. **Script.** Hook, narrative, pattern interrupts, call to action, chapters, title variants, description, tags; every factual claim marked with its source or "unverified". Saved as a draft in Scout's folder, shown in the desktop and summarised in chat.
4. **Narration.** Text to speech through the AI Source already connected in Scout when it offers speech (the same credential, no second account); otherwise a local engine on the machine; per-scene audio files; the owner can replace a sentence and regenerate one scene.
5. **Visuals.** Default: a slideshow built from images the connected AI Source generates (when it offers images) or the owner supplies, with captions; optional: video generation through that same source when it offers it, with a cap on paid seconds and a fallback to the slideshow. Scene list with lock, reorder, replace and regenerate.
6. **Assembly.** `ffmpeg` renders the MP4 (narration, visuals, captions, music the owner supplies) and a 9:16 Shorts cut per scene group; the renderer is our own C# driver of the `ffmpeg` binary bundled with the desktop (one external tool, not a library, chosen because no own renderer is realistic; the owner's rule on third-party code is met by owning every line that drives it).
7. **Review.** The Production screen shows the script, the narration, each scene and the final video; checks run (duration, loudness, caption coverage, no placeholder media); the owner edits, regenerates or approves. Nothing leaves the machine before this.
8. **Publish.** The approved package goes to the approval queue: upload as private or unlisted, set metadata and thumbnail, schedule the publish time; each is an approved action; the result and the video id are logged and shown.
9. **Learn.** At 24 hours and 7 days Scout reads the Analytics API for the video and compares it with the channel's own history (the benchmarks page of the read-only plan); it proposes changes (titles, thumbnails, topics) the owner accepts or rejects; accepted ones become experiments.
10. **Engage.** Comment sync, themes and questions, spam quarantine, reply drafts; publishing a reply is an approved action.

### Where it runs

The owner's rule (2026-11-02): "a maior parte tem que ser em C#". Every stage of the pipeline is C# in the .NET worker and control plane: research, script, narration and image calls through the connected AI Source, the scene list, the `ffmpeg` driver, the checks, the scheduler (a hosted service in the worker), the approval queue, the write log and every YouTube call. The desktop (TypeScript) only shows and asks: the Production and Review screens render what the control plane returns and send the owner's grant, edits and approvals back over the authenticated socket; it holds no pipeline logic. Chat is the remote control: ask for a video, follow progress, get the report.

## Phases

### Phase 1: the contract in the product
- **Scope:** ADR 0032 wording on README, the site and CLAUDE.md; the `youtube` catalog entry declares the rows above; the YouTube card shows them with the grant line and an Allow writes button behind a confirmation dialog; the approval queue and the write log in the control plane and the desktop Review screen (empty for now); the LLM guard refuses writes only when no granted feature exists. Nothing on the write side executes yet.
- **Success criteria:** granting writes on the YouTube card shows every write row and needs the confirmation; a test proves a write row under `WriteClass.None` fails the catalog test; approvals exist only as control-plane calls the desktop makes.

### Phase 2: mandate, research and script (no write)
- **Scope:** Production screen with the mandate; research functions and the script generator; drafts in Scout's folder; the chat flow "make a video about X" up to the script.
- **Success criteria:** the target outcome's first half: a script the owner can read on the desktop within minutes of asking in Discord, grounded in the channel's data and the benchmarks page.

### Phase 3: narration and visuals
- **Scope:** TTS provider card and the owner's local engine; image provider card; scene list with regenerate, lock, reorder; slideshow visuals; paid video provider as an opt-in with a cap.
- **Success criteria:** every scene has audio and a visual, replaceable one by one; no paid second spent beyond the cap.

### Phase 4: assembly and review
- **Scope:** the `ffmpeg` driver, MP4 and Shorts rendering, captions, the checks, the review screen.
- **Success criteria:** a real MP4 the owner would upload by hand, with the checks green; placeholder media cannot be approved.

### Phase 5: publish and learn
- **Scope:** the upload scopes on the sign-in, the approval queue in use, scheduling, the 24-hour and 7-day reports, experiments.
- **Success criteria:** the target outcome.

### Phase 6: engage
- **Scope:** comment sync, themes, spam quarantine, reply drafts and approved replies.

## Decisions pending

- [[pages/decisions/0032-per-feature-write-permissions]] accepted; ADR 0030 is amended when the first write row is implemented (phase 5).
- **Owner, decided 2026-11-02:** narration and images come only from the AI Sources already connected in Scout; no separate account, key or card for media ("o objetivo e' sempre usar apenas os AI Sources que estao autenticados ja no Scout e nao ter uma conta a parte"). Phase 3 therefore carries a capability matrix per connected provider (which of them offers speech and image generation through the same credential, which do not) and a local fallback for the rest: text-card slideshow with owner-supplied images, and a local speech engine when one is installed on the machine.
- **Owner:** whether `ffmpeg` bundled with the desktop is acceptable as the one external tool; the first channel mandate.

## Services affected

- [[pages/services/control-plane]] and [[pages/services/host-control-plane]]: feature rows, grants, the approval queue, the write log.
- [[pages/services/desktop-ui]]: the YouTube card's grant, the Production and Review screens, the approval surface; no pipeline logic.
- [[pages/services/plugin-youtube]]: new scopes and functions per feature.
- [[pages/services/llm]]: the guard and the provider cards for TTS and images.
- [[pages/services/conversation]]: the chat flow and progress messages.

## Risks

- Google verification: the `youtube.upload` scope is restricted and the app must pass Google's review before more than the test users can grant it; while unverified the owner still can.
- Paid providers: a runaway loop can spend money; every paid call checks the cap first and the cap is per day.
- Content rights: music and images the owner supplies are the owner's responsibility; generated media is marked as generated in the metadata.
- The worker must be running for the scheduler; a missed slot is reported, never silently skipped.
