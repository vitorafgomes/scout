---
title: "Follow-ups"
version: "1.0"
date: 2026-10-21
changes: "Initial tracker page"
page_type: roadmap
status: active
description: "Deferred work found while doing something else: each entry names the blocker, the evidence and what unblocks it."
source:
  - chat
reliability: medium
---

# Follow-ups

> Deferred work found during other changes. Each entry carries its own date; close an entry by deleting it in the change that resolves it.

## Scope

- **In scope:** small, concrete deferrals with a known unblock condition.
- **Out of scope:** initiatives with phases (they get their own page under `pages/roadmap/`).

## Entries
### Code identifiers are one token in the full-text index (2026-11-07)

- **Blocker:** deferred by the owner on 2026-11-07; G3 shipped with the default tokenizer ([[pages/roadmap/gap-plan]]).
- **Evidence:** `chunk_fts` uses the `unicode61` tokenizer (`src/Scout.Knowledge/SqliteProjectKnowledgeStore.cs:23`), which splits on non-alphanumerics only, so `ApplyDiscount` is one token and a question with "discount" does not rank the method that applies it when the provider has no embeddings ([[pages/services/knowledge]]).
- **Unblocks with:** a second indexed column holding each chunk's identifiers split at case changes and digits (`ApplyDiscount` as `apply discount`), written by `ReplaceTextRowsAsync`, with a ranking test that "discount" finds `ApplyDiscount`; measured on a real repository against today's ranking before it ships.

### A structural graph over a repository snapshot (2026-11-06)

- **Gap:** a review reads a snapshot file by file and searches it by text or by embedding ([[pages/decisions/0039-repository-snapshot-under-the-data-folder]]); "who calls this", "what breaks if this changes" and "where are the hubs" still cost many reads. The owner asked on 2026-11-06 whether a graphify-style graph (AST nodes and edges, god nodes, blast radius) should be a third toolset.
- **Unblocks with:** one real review on a snapshot first, to see which questions the reads and the index leave unanswered; then a graph built once per snapshot under its folder and three functions (callers, affected, hubs), weighed against the build cost per snapshot.

### Every local `update` carries the whole reply so far (2026-11-06)

- **Gap:** the sink contract is cumulative for Teams's sake, and the local adapter writes the full text on every `update` (`src/Scout.Adapter.Local/LocalResponseSink.cs:47`), so a long reply streaming to the desktop costs O(n²) bytes on the worker's stdout; harmless at today's sizes (32,000-character cap), waste at the pace of a code review ([[pages/services/adapter-local]], [[pages/decisions/0013-conversation-core-streaming-sink]]).
- **Unblocks with:** an `append` event on the local wire only, the sink sending what grew since its last write and main concatenating; the core's contract untouched; measured first on a 30 KB reply.

### Main re-sends the whole conversation to every window on every event (2026-11-06)

- **Gap:** each event reshapes one turn and broadcasts the whole list, up to 200 turns with their replies, to every window (`ui/packages/main/src/quick-ask/quick-ask.ts:821`); with replies now rendered from Markdown each push costs more on the renderer side ([[pages/services/desktop-quick-ask]]).
- **Unblocks with:** a per-turn push (`scout:local.turn` with the one turn that changed) that the renderer patches into its list, the full list kept for the first load and a clear; measured first with a long conversation open in both windows.

### The presenter description cannot be given from the chat (2026-11-04)

- **Gap:** `youtube_make_video` starts an `animated_character` project with an empty presenter description, so its presenter picture is drawn from the direction and the topic alone (`src/Scout.Operator/OperatorPipeline.cs:378`); only Scout Desktop's Start, Produce and Delegate take the description ([[pages/services/operator]], [[pages/services/desktop-operator]]).
- **Unblocks with:** a `presenter` parameter on `youtube_make_video` and on the owner's produce and delegate functions, owner only like the direction, at most 300 characters, carried to the control plane's start and produce requests, with a plugin test that a non-owner's presenter starts nothing.

### The animated character never blinks or turns its head (2026-11-04)

- **Gap:** the assembler lays only the mouth over the presenter picture (`src/Scout.Operator/Assembler.cs:298`), so the face is otherwise frozen for the whole scene ([[pages/services/operator]]).
- **Unblocks with:** an eye box the owner marks beside the mouth box and a closed-eyes picture laid over it for about 150 ms every 3 to 6 seconds, timed by the scene's length so the segment key stays stable, with the overlay asserted in an assembler test; head turns wait for the realistic photo animation spike of phase 8c.

### Only one mouth style (2026-11-04)

- **Gap:** every animated character speaks with the same nine cartoon mouths Scout Desktop bundles (`SCOUT__OPERATOR__MOUTHSPATH`), whatever the presenter picture's style ([[pages/services/operator]], [[pages/services/desktop-operator]]).
- **Unblocks with:** a second set of nine shapes under its own folder, chosen per project and stored like the motion, its name joining the presenter segment's identity (`src/Scout.Operator/Assembler.cs:410`), with a segment-key test that a changed style renders the scene again.

### Mouth shapes drawn to match the presenter picture (2026-11-04)

- **Gap:** the bundled mouths are drawn once for every character, so their colour and line can differ from the picture the image service made ([[pages/services/operator]]).
- **Unblocks with:** an image-edit request per shape against the presenter picture's mouth box, nine pictures saved in the media folder and counted in the 40 pictures a day, behind the readiness of an image-edit service; measured against the bundled set on a few pictures before it is the default.

### Rhubarb on macOS is not verified (2026-11-04)

- **Gap:** Rhubarb Lip Sync 1.14.0 publishes a macOS archive, but no run of Scout Desktop's macOS build has shown that the bundled program starts there, signed and outside quarantine, and answers `--version` ([[pages/services/desktop-operator]]); until then the readiness of lip sync on a Mac is unproven (`src/Scout.Operator/RhubarbLipSync.cs:62`).
- **Unblocks with:** one run of the macOS build that reports `lip_sync_available` and renders a presenter scene with its mouth moving, recorded on [[pages/services/desktop-operator]].

### Regenerate picture is offered while the project runs (2026-11-04)

- **Gap:** the control plane refuses `RegeneratePresenter` on a `producing` or `assembling` project with "A project that is producing or assembling cannot have its presenter picture regenerated; wait for it to finish or fail." (`tests/Scout.ControlPlane.Tests/Operator/OperatorServiceTests.Character.cs:334`), but the Presenter block's "Regenerate picture" is disabled only while a media action runs or the AI Source makes no pictures (`ui/packages/renderer/src/app/production/production.component.ts:661`), so the owner learns it from the refusal under the block ([[pages/services/desktop-operator]]). The same holds for "Save mouth": the control plane refuses a mouth box on a `producing`, `production_queued` or `assembling` project with its own sentence, and the button stays enabled there.
- **Unblocks with:** both buttons disabled on the running statuses with the control plane's sentence as their title, asserted in `production.component.spec.ts`.

### The presenter composite is blended at 30 frames a second though it changes once a second (2026-11-04)

- **Gap:** a presenter scene over the project's background turns the background to 30 fps before the presenter picture is laid over it (`src/Scout.Operator/Assembler.cs:322`), so the overlay of a picture that only changes with the background's one frame a second runs on every output frame ([[pages/services/operator]]).
- **Unblocks with:** the picture laid over the background before `fps=30`, measured with `ffmpeg -benchmark` on a 10 s scene against today's graph, and taken only if it is faster; it changes the presenter graphs every assembler test asserts, so the argument lists are updated in the same change.

### A failing rhubarb runs again on every assembly (2026-11-04)

- **Gap:** a rhubarb failure or timeout leaves no cues file, and only a cues file newer than the narration skips the run (`src/Scout.Operator/RhubarbLipSync.cs:95`), so a narration rhubarb cannot read is run again, up to its timeout, on every assembly of the project ([[pages/services/operator]]).
- **Unblocks with:** a failure marker beside the cues, `mouth-cues-<scene>.failed`, newer than the narration, read like the cues so the scene is shown still with its note without running rhubarb, and removed when the narration changes; with a test that a second assembly of a failing scene runs rhubarb once.

### The learning loop's thumbnail prompt repeats the no-real-person clause (2026-11-04)

- **Gap:** the thumbnail and the presenter picture end their prompts with one constant, `VisualGenerator.NoRealPerson` (`src/Scout.Operator/VisualGenerator.cs:152`), while the learning loop's thumbnail proposal writes the same words inline (`src/Scout.Operator/LearningLoop.cs:294`), and no test pins that prompt ([[pages/services/operator]]).
- **Unblocks with:** a learning-loop test asserting the prompt the image service receives ends with the clause, then the inline words replaced by the constant.

### The Production screen reads every project in full every 10 seconds (2026-11-04)

- **Gap:** the Production screen refreshes the whole project list every 10 seconds (`ui/packages/renderer/src/app/production/production.component.ts:31`), and every project comes with its script, claims and scenes; an `own_video` project with its timed transcript and scenes is about 100 to 130 KB, so a list of a few long recordings is megabytes over the socket every 10 seconds ([[pages/services/desktop-operator]], [[pages/services/operator]], phase 8b of [[pages/roadmap/youtube-channel-operator]]).
- **Unblocks with:** a list summary rpc that answers id, topic, status, format, progress and `UpdatedAt` per project, the full project read only for the one the owner opens, with a client test for the summary and a contract test for the rpc.

### A long recording holds the speech model and its samples for the whole transcription (2026-11-04)

- **Gap:** the operator hands the whole recording's WAV to the transcriber in one call, so the transcriber's single gate is held for the whole transcription and a voice message gets the busy sentence after 30 seconds, and the samples of the whole recording, about 230 MB at 60 minutes, are held beside the model ([[pages/services/transcription-whisper]], [[pages/decisions/0024-voice-messages-transcribed-locally]], [[pages/services/operator]]).
- **Unblocks with:** transcribing in 5-minute windows, each its own `TranscribeSegmentsAsync` call with its offset added to the segments' times and the gate released between windows, so a voice message waits for one window at most and only one window's samples are held; with a test that two windows give one transcript with continuous times and a voice message answered between them.

### Captions in a project of the owner's own video cannot be turned off, and drift a little from the speech (2026-11-04)

- **Gap:** in `own_video` the scene's text is always drawn as captions over the recording, and the assembler spreads them evenly over the scene rather than at each spoken segment, so a caption runs a little ahead of or behind the words; an owner whose recording already carries its own titles, or who wants none, has no way to leave them out ([[pages/services/desktop-operator]], [[pages/services/operator]], phase 8b of [[pages/roadmap/youtube-channel-operator]]).
- **Unblocks with:** a per-project caption toggle, chosen in the Produce panel and stored on the project like the motion, that the assembler reads; and captions timed from the transcript segments, which the script keeps with their start and end only until the scene plan rewrites it as one section per chapter, so the segment times would need to stay on the scenes.

### A "This is me" mark for the owner's chat ids (2026-11-04)

- **Gap:** the operator's owner-only functions from the chat answer an allowed chat user only when `Scout:Access:OwnerUserIds` lists that id ([[pages/decisions/0021-allowed-users]], amendment of 2026-11-04), and Scout Desktop sets no such list yet, so on Discord and Slack nobody is the owner and the owner acts from the quick ask only.
- **Unblocks with:** a "This is me" mark beside an id in Settings, "Who can use Scout", that main validates like the allow-list and passes as `SCOUT__ACCESS__OWNERUSERIDS__<n>`, restarting a running worker, with the desktop tests for the mark and the variable.

### The own_pictures text-card decision lives in the production loop, not in VisualGenerator (2026-11-04)

- **Gap:** `OperatorPipeline` masks `imagesNow` for `own_pictures` and builds a synthetic `VisualResult(TextCard, null, NoOwnPicture)` beside `VisualGenerator`, which already answers `TextCard` with a note for `NoImages` and `PastMaxScenes`; a third format (the AI presenter of 8c) would add another branch there ([[pages/services/operator]]).
- **Unblocks with:** passing the project format to `VisualGenerator.GenerateAsync` and answering the `own_pictures` card there, when 8c touches the generator.

### Editing the first scene of a section can drop its chapter (2026-11-04)

- **Gap:** `src/Scout.Operator/PackageChecks.cs:261-265` anchors a chapter by finding the scene's cleaned narration inside its section's narration, so an edited text on a section's first scene no longer matches and the chapter is dropped, and the whole chapter list if fewer than 3 remain ([[pages/services/operator]]). Since 2026-11-04 a scene of the owner's recording is anchored by its clip start instead, so this stays open for the narrated formats only.
- **Unblocks with:** anchoring chapters by scene id instead of narration text, with a test that edits the first scene of a section and checks the chapter survives.

### An approved upload action survives a scene edit (2026-11-04)

- **Gap:** an approved `videos.insert` stays queued after a scene edit, regenerate or attach; only `ApprovedAt` blocks it at execution, so once the owner re-approves the edited package the old approval runs ([[pages/services/control-plane]], ADR 0032). Pre-existing before 8a.
- **Unblocks with:** `AfterSceneEdit` cancelling pending upload actions of that project, with a test.

### A long meeting keeps only its newest 500 lines (2026-11-04)

- **Gap:** the transcript store holds 500 lines and the Meetings screen lists the newest 100, while the worker accepts 5,000 lines per meeting ([[pages/services/desktop-quick-ask]], [[pages/services/meetings]]).
- **Unblocks with:** a larger store while a meeting runs, or paging in the list, with tests.

### Done during a streaming reply keeps a partial answer (2026-11-04)

- **Gap:** a turn whose reply is still streaming or failed is kept with the partial text or the failure sentence as its answer ([[pages/services/desktop-quick-ask]], [[pages/services/meetings]]).
- **Unblocks with:** waiting for the reply or marking the turn unfinished, with a test.

### A meeting line over 2,000 characters gets the generic limits sentence (2026-11-04)

- **Gap:** the store refuses a line over 2,000 characters, but the local adapter's sentence for a refused `keep` names only the lines, the turns and the title ([[pages/services/meetings]], [[pages/services/adapter-local]], [[pages/decisions/0037-meetings-kept-locally-in-sqlite]]).
- **Unblocks with:** a sentence naming the line length, with a test that a `keep` holding a 2,001-character line answers it.

### The meetings WAL checkpoint is a no-op while another connection reads (2026-11-04)

- **Gap:** a `meetings` request racing a `delete` can leave the deleted text in `meetings.db-wal` until the next delete, as the ADR states ([[pages/services/meetings]], [[pages/decisions/0037-meetings-kept-locally-in-sqlite]]).
- **Unblocks with:** retrying the checkpoint on the busy flag, or failing the delete, with a test that holds a read open during a delete.

### A meeting at every limit can exceed the local adapter's request line (2026-11-04)

- **Gap:** 5,000 lines of 2,000 characters with their translations passes the 20 MB stdin line the local adapter accepts, so a meeting at every limit is refused as an oversized request ([[pages/services/adapter-local]], [[pages/services/meetings]], [[pages/decisions/0037-meetings-kept-locally-in-sqlite]]).
- **Unblocks with:** the desktop capping what it sends, or the adapter accepting a chunked keep, with a test at the limit.

### The OCR bundle ships without the licence texts of its other bundled libraries (2026-11-04)

- **Gap:** the tesseract AppImage carries only the tesseract Apache-2.0 `LICENSE` and `AUTHORS`; glibc, libstdc++, libgomp, libarchive, libxml2, nettle and the image libraries ship with no notice, and the tree is 54 MB with six unused languages and man pages ([[pages/decisions/0036-text-read-from-a-screenshot-by-a-local-ocr-program]], [[pages/services/desktop-operator]]). The leptonica licence is the one text that ships beside the program.
- **Unblocks with:** a first-party static build in CI, or a pruned tree with each library's notice collected at build.

### The transcript sent with a question enters the conversation memory (2026-11-04)

- **Gap:** the quick-ask window appends the last 10 minutes of the transcript (up to 24,000 characters) to the question text, and the worker stores the whole text as the turn, so every follow-up turn replays it to the provider ([[pages/services/desktop-quick-ask]], [[pages/services/adapter-local]], [[pages/decisions/0034-live-transcription-on-the-local-adapter]]).
- **Unblocks with:** a `context` field on the ask line that the adapter passes to the model request and leaves out of the stored turn, with a test that a follow-up turn's history holds the bare question.

### Live sessions are not stopped when the local adapter shuts down (2026-11-04)

- **Gap:** `LocalChatAdapter.StopAsync` cancels the lifetime token but leaves any session still in `_sessions` to its own end ([[pages/services/adapter-local]], [[pages/decisions/0034-live-transcription-on-the-local-adapter]]); the desktop stops every session before it stops the worker, so nothing is lost today, but a worker stopped mid-session never writes `complete` for it. Present since phase 1 with one session; the two-session change (review of 2026-11-04) made it visible.
- **Unblocks with:** `StopAsync` stopping every open session and writing their `complete` events before the token is cancelled, with a test that stops the adapter while a session is open.

### A listen that reuses an id just stopped can share that id with the old session's last final (2026-11-04)

- **Gap:** `stop` frees the id as soon as it is removed, while the old session's last final may still be decoding; a new `listen` with the same id is accepted and both could write events under one id. The desktop never reuses an id, so this cannot happen from Scout Desktop.
- **Unblocks with:** refusing a `listen` whose id is still stopping, or the desktop contract stating that ids are never reused; one test that stops and immediately re-listens under the same id.

### A revoke and re-grant during an upload can let a second upload be queued (2026-11-03)

- **Gap:** `PublishPackageAsync` ignores an executing `videos.insert` whose claim predates the current grant (`!Stale(a, grantedAt)` in [[pages/services/control-plane]]), so after the owner revokes YouTube writes mid-upload and grants again, the publication reads failed and a second Publish queues a second upload while the first may still finish on YouTube (`/code-review medium` of 2026-11-03). Rare: it needs a revoke and a re-grant inside one upload.
- **Unblocks with:** the executing check counting a stale executing insert as still running, with a test that revokes, re-grants and publishes again.

### A retryable action failure waits for the 30-minute claim lease, not the next tick (2026-11-03)

- **Gap:** `ActionExecutor` returns without recording on a retryable failure, so the action stays executing until `ClaimLease` expires ([[pages/services/operator]], [[pages/services/control-plane]]); a `thumbnails.set` waiting on an upload recorded "succeeded, not saved" is re-handed every 30 minutes with no end, and the project cannot be published again while its publication is `uploading`.
- **Unblocks with:** a bounded wait (the thumbnail giving up after N attempts with a readable reason) and a shorter re-hand for a retryable failure, with a test on the "not saved" upload path.

### Using an audience idea starts the project before the idea is marked used (2026-11-03)

- **Gap:** `DecideIdeaAsync(use: true)` calls `StartProjectAsync` first and writes `comments.json` after ([[pages/services/control-plane]]); a comments write that fails leaves a queued project and a still-proposed idea, and a retry starts a second project on the same text.
- **Unblocks with:** marking the idea used with the project id in the same gated write, or starting the project only after the comments write succeeded and tolerating a used idea without a project.

### The write log parses each tail line twice (2026-11-03)

- **Gap:** `JsonLinesWriteLog.ListSinceAsync` deserializes every line in its stop predicate and again in `ListCoreAsync`, on every `RecordAsync` of an approved action ([[pages/services/control-plane]]). Measured nothing; estimated thousands of lines per record on a busy operator.
- **Unblocks with:** `ReadTailAsync` handing the parsed entry to the stop predicate, with the existing log tests green.

### YouTube metadata limits and the media file-name rule live in three places (2026-11-03)

- **Gap:** `PackageRules` (control plane) re-implements `PackageChecks` (worker) for the title, description, tag and chapter limits, and the media file-name rule exists in `SceneRules.ValidateFileName`, `MediaFiles.PathFor` and the desktop's `isSafeMediaName`; the comment-id regex exists in `CommentRules`, `CommentDesk` and `operator.ts` ([[pages/services/control-plane]], [[pages/services/operator]], [[pages/services/desktop-operator]]). Drift makes the worker and the control plane disagree on a package.
- **Unblocks with:** one shared static for the limits in `Scout.ControlPlane.Contracts` (the only assembly both sides reference) and contract tests pinning the desktop copy to it.

### `NextQueuedProjectAsync` reads the projects file twice per poll (2026-11-03)

- **Gap:** a pre-check read precedes `UpdateProjectsAsync`, which re-reads under the gate and already skips the write when the callback answers null ([[pages/services/control-plane]]); the worker polls every 30 s.
- **Unblocks with:** dropping the pre-check, with the queued-project tests green.


### `check-diagnostics` reports two SYSLIB1092 in the control-plane host (2026-11-03)

- **Gap:** `scripts/check-diagnostics.sh` fails on `src/Scout.Host.ControlPlane` with two info-level SYSLIB1092 at `NativeUnix.cs:123`, which were already there before phase 6, and so on `tests/Scout.ControlPlane.Tests`, which references the host. It now fails on `tests/Scout.Host.Console.Tests` too, because that test project references the host for the 16 MB round trip ([[pages/services/host-control-plane]]). The build is still at 0 warnings, since info diagnostics never fail it ([[pages/conventions/tooling]]).
- **Unblocks with:** the change the SYSLIB1092 message names on the `stat` `LibraryImport` declaration in `NativeUnix`, with a test of the native call.

### Some comment fields have no length limit in the control plane (2026-11-03)

- **Gap:** the control plane bounds a comment's text, draft, theme and id, but not its `Author`, `VideoTitle`, `ThreadId`, `VideoId` or `Error` ([[pages/services/control-plane]]). A worker that misbehaves could grow `comments.json` up to the 16 MB message limit. The security review of phase 6 marked this as hardening only: strangers cannot reach it, and the worker already holds the owner's token.
- **Unblocks with:** limits in `CommentRules` for those five fields (YouTube's own display-name and title lengths), with a test, in the next change that touches the comment rules.

### The comment poll reads the whole comments state every minute (2026-11-03)

- **Gap:** the worker's comment poll needs only the owner's sync request and the comments marked for a draft, but `GetComments` has one full-state shape, so it reads and maps up to 1000 comments every 60 s under the operator store's single gate ([[pages/services/operator]], [[pages/services/control-plane]]). The performance lens measured nothing and estimated about 0.5 MB a minute at 1000 comments, a fraction of a percent of one core.
- **Unblocks with:** a measured cost on a busy channel; then an optional `pending_only` flag on `ListCommentsRequest` that answers the sync fields and the `draft_requested` comments only.

### The 6-hour clearing can undo an approval made in the same moment (2026-11-03)

- **Gap:** the control plane's clearing reads the live comment actions before it takes the store's gate; in the few milliseconds between `ApproveReply` writing `reply_approved` and queueing its action, the clearing moves the comment back to `drafted`, and the executor records that action failed without posting ([[pages/services/control-plane]]). Nothing is posted wrongly; the owner approves again.
- **Unblocks with:** reading the live actions inside the same gate as the comments write, which needs the action queue and the operator store to share one lock or a two-phase check.

### YouTube does not say how `order=time` sorts comment threads (2026-11-03)

- **Gap:** Google's `commentThreads.list` reference (read 2026-11-03) does not say whether `order=time` sorts by the top-level comment's publish time or by the thread's latest activity. The sync reads until a whole page is older than its window, at most 5 pages, and keeps threads by the top-level comment's time ([[pages/services/plugin-youtube]]), so a channel with more than 500 threads touched inside one window can miss the oldest of them until the next window.
- **Unblocks with:** an observation on a real channel with replies on old threads; then either a larger page budget or a per-video read for the videos the scan stopped on.

### The revoke sentence is not shown on a write recorded after a revoke (2026-11-03)

- **Gap:** when the owner revokes YouTube writes while an approved action is running, the executor saves what YouTube took (the video id or the arm timing) and records the action once as "after revoke" ([[pages/invariants/no-executive-writes]], [[pages/services/operator]]); the record consumes the action, so the executor's follow-up save of "The owner revoked YouTube writes after approving this action" is refused and only logged. The write itself is shown and logged correctly; the project just does not say why.
- **Unblocks with:** the control plane's after-revoke record path writing that sentence on the action's project in the same step ([[pages/services/control-plane]]).

### No LGPL ffmpeg is bundled for macOS (2026-11-03)

- **Blocker:** the operator's assembly needs ffmpeg (ffprobe is no longer used). Linux and Windows bundle the BtbN LGPL static builds (libopenh264, AAC, libfreetype), pinned by SHA-256; no reputable LGPL static build exists for macOS arm64 (martin-riedl, evermeet and osxexperts builds are GPL, Homebrew is GPL), so on macOS Scout uses an ffmpeg the owner installed and the readiness banner says when none is found ([[pages/decisions/0033-channel-operator-architecture]], [[pages/services/operator]]).
- **Unblocks with:** a CI job on a macos runner that builds ffmpeg from a pinned tag with an LGPL configure (no `--enable-gpl`, `--enable-videotoolbox`, libopus, libfreetype) and publishes it as a pinned release asset.
- **Also noted:** the bundled H.264 encoder is a self-compiled libopenh264; Cisco's patent licence covers only Cisco's own binary, so the owner carries the same H.264 patent position as any project that ships an H.264 encoder; VP9 with Opus is the runtime fallback.

### One-click update covers only the AppImage and Windows (2026-11-02)

- **Blocker:** Update and restart ([[pages/decisions/0031-desktop-self-update]]) needs a package the updater can replace. On macOS, electron-updater applies an update only to a signed and notarized app and reads a `zip` target the build does not produce, and the builds are unsigned (see "Desktop builds are unsigned" below). A `.deb` has no in-place updater: electron-updater's `DebUpdater` installs through `pkexec` or `sudo` and `dpkg`, which is a system package change, not Scout's own file. Both keep the Download button (`canSelfUpdate`, `ui/packages/main/src/update/update-notice.ts:62`).
- **Unblocks with:** macOS: the signing and notarization in the unsigned-builds entry, plus a `zip` mac target and keeping `latest-mac.yml` and the mac blockmaps in `.github/workflows/release.yml:120`. `.deb`: an apt repository the package manager updates from, which is outside Scout's own installation and needs its own ADR.
- **Also noted:** an AppImage whose file name carries a version is replaced under the new release's name, and the old file is deleted (electron-updater's `AppImageUpdater.doInstall`); `refreshLoginEntry` (`ui/packages/main/src/shell/login-item.ts:102`) rewrites the start-at-login entry on the next start.

### Desktop builds are unsigned (2026-11-02)

- **Blocker:** no signing identities. The release workflow packages with `CSC_IDENTITY_AUTO_DISCOVERY` false and the mac config sets `identity: null` (`ui/build/electron-builder.yml:82`), so macOS Gatekeeper refuses the dmg until the user allows it in System Settings, and Windows SmartScreen warns on the installer. The site and the README say so ([[pages/decisions/0029-project-site-and-releases]]).
- **Unblocks with:** an Apple Developer ID certificate plus notarization (an App Store Connect API key, `hardenedRuntime: true`), and a Windows code-signing certificate (an OV or EV certificate, or Azure Trusted Signing), each stored as a GitHub Actions secret and read by electron-builder in the `build` job of `.github/workflows/release.yml:32`.

### Intel Mac package is built but not released (2026-11-02)

- **Blocker:** the mac leg of the release workflow publishes only the `osx-arm64` worker, while the desktop looks for an `osx-x64` worker on an Intel Mac (`ui/packages/main/src/worker/resolve-binary.ts:17`), so the x64 `.dmg` electron-builder produces could not start Scout. The release workflow deletes it before publishing and the site offers the Apple silicon image only.
- **Unblocks with:** a second `bun run worker:publish -- osx-x64` on the mac leg and both workers carried in each dmg, or a separate x64 leg; then stop pruning `*-mac-x64.dmg` and show the Intel row the page already knows how to label.

### Project site has no screenshots yet (2026-11-02)

- **Blocker:** none; nobody has captured them. The hero of `docs/index.html` shows an example Discord conversation built in HTML instead of a screenshot of the desktop app or a real chat.
- **Unblocks with:** screenshots of the desktop app and of a Discord or Slack answer, with no real names, hosts or ids, saved under `docs/` and placed next to the example conversation or in the "How it works" section.

### `ProjectWatchServiceTests.RapidEdits_AreDebouncedToSingleReindex` is timing-sensitive (2026-11-02)

- **Blocker:** none; it is flaky, not wrong. The test drives a real file watcher with a 50 ms debounce (`tests/Scout.Knowledge.Tests/ProjectWatchServiceTests.cs:11`) and expects one `ReplaceFileAsync` for a burst of edits; on a shared GitHub runner it counted two (run 41435925678, 2026-11-02) and passed on rerun.
- **Fix:** drive the debounce from an injected clock or a `TimeProvider` so the burst and the quiet period are deterministic, or widen the debounce under test well past the watcher's own coalescing window.

### X11 always-on-top unverified on COSMIC (2026-10-31)

- **Blocker:** the owner's run of the AppImage. On Linux under Wayland with XWayland, Scout relaunches once with `--ozone-platform=x11` (`ui/packages/main/src/shell/x11-relaunch.ts:12`, `ui/packages/main/src/main.ts:122`) so the floating button and the quick-ask window are placed in the chosen corner and stay on top through XWayland. The corner and display choice are measured to hold (below); what stays open is only whether the bubble stays above native Wayland windows, visible only on screen. The second start costs one extra process launch on every Wayland start.
- **Fix:** none expected; what remains open is the owner confirming on screen that the bubble stays above native Wayland windows. If it does not, record it under ADR 0028.
- **Measured 2026-10-31 with Electron 45 under `--ozone-platform=x11`:** every window COSMIC's X window manager manages, whatever its type, is placed where COSMIC chooses, ignores `setPosition` and `setBounds` before and after `show`, reports not always-on-top and gets a server-side frame. A window created with `focusable: false`, shown, then made focusable and focused stays unmanaged, keeps its position and always-on-top, and `document.hasFocus()` reported focus to its page, but that was not real X keyboard focus (see the last bullet).
- **Measured 2026-10-31 on the owner's COSMIC session** (AppImage started with `--ozone-platform=x11`, read with `xwininfo` and `xprop` on `DISPLAY=:1`): the bubble window was 64x64 at +1840+1190, the bottom-right corner of the 1920x1080 laptop display (offset +0+190) minus the 16 px margin, so the corner and the display choice hold. COSMIC's X window manager reports itself as "Smithay X WM" and lists `_NET_WM_STATE_ABOVE` and `_NET_WM_STATE_SKIP_TASKBAR` in `_NET_SUPPORTED`. The bubble carries no `WM_STATE` and is absent from `_NET_CLIENT_LIST_STACKING`: with `focusable: false` Electron creates it as a window the X window manager does not manage, which the compositor draws above managed windows. Whether that holds above native Wayland windows is only visible on screen.
- **Measured 2026-10-31 on the owner's COSMIC session:** an unmanaged window never receives keyboard focus there: `_NET_ACTIVE_WINDOW` on the root window stays 0 while it is shown, and Electron's `focus()` on X11 only asks the window manager, which ignores a window it does not manage. The owner could not type in the quick-ask window or close it. The quick-ask window is now managed on Linux (`ui/packages/main/src/quick-ask/quick-ask.ts:690`), so on COSMIC it loses the corner and gets a frame, while the bubble keeps the corner. Keeping the corner and the keyboard there needs a window the compositor positions (a Wayland layer-shell surface or a COSMIC-specific protocol), which Electron does not offer.

### Voice transcript shown inside the reply, not as the question (2026-10-31)

- **Blocker:** no worker event carries the transcript. The owner's request is deferred, not done: a voice turn's question is "(voice message)" (`ui/packages/main/src/quick-ask/quick-ask.ts:114`), and what Scout heard reaches the owner only as the reply's opening `> (voice) ` quote, which the worker writes once the reply starts. The owner asked to see what was heard so they can check it ("deveria mostrar o que falei, para que eu veja se esta correto").
- **Fix:** a `heard` event from the local adapter, carrying the transcript before the reply, that main sets as the turn's question; the reply then drops the quote for local turns. It touches the worker's local protocol and main's turn handling, with a test on each side.

### Floating button cannot be dragged (2026-10-31)

- **Blocker:** none. The floating button sits only in one of four corners, 16 px from the edges of the chosen display's work area (`ui/packages/main/src/quick-ask/placement.ts:11`); the owner cannot move it out of the way of something in that corner ([[pages/decisions/0028-local-quick-ask-assistant]]).
- **Fix:** pointer-drag in `FloatingButtonComponent` through an IPC that moves the bubble window from main, telling a drag from a click, and remember the offset per display under `quick-ask.bubble`; a click still opens quick ask. Wayland stays compositor-placed.

### Floating button does not follow display changes (2026-10-31)

- **Blocker:** none. The bubble is placed when it is created, when its settings change and each time the quick-ask window hides (`ui/packages/main/src/quick-ask/quick-ask.ts:623`); unplugging a display, or a resolution or scale change, can leave it off screen or away from its corner until then.
- **Fix:** reposition it on `screen`'s `display-metrics-changed` and `display-removed` events, removing the listeners on dispose, with a test that emits each event on the fake screen.

### Clear on the Chat screen keeps the worker's local memory (2026-10-30)

- **Blocker:** no channel for it. Clear empties only the conversation main holds for the quick-ask window and the Chat screen (`ui/packages/main/src/quick-ask/quick-ask.ts:860`); the worker keeps the `local` channel's turns in `IConversationMemoryStore` until they expire or the worker restarts, so a question after Clear can still draw on the cleared conversation ([[pages/decisions/0028-local-quick-ask-assistant]]).
- **Fix:** let Clear also reset the worker's memory for the local channel, for example a `reset` request on the quick-ask stdin protocol that the local adapter turns into a memory reset; it touches only Scout's own state.

### Provisioning pages still describe the old Discord flow (2026-10-28)

- **Blocker:** none. `pages/architecture/electron-provisioning-ui/per-platform-feasibility.md:52` to `:54` and `pages/architecture/desktop-ui-design-system/5-screens/screen-5-discord-wizard-step-3-oauth2-install.md` still describe the manual intent toggle and the loopback OAuth install, which [[pages/decisions/0022-discord-setup-from-the-bot-token]] replaced with the bot-token setup and a plain `scope=bot` invite.
- **Fix:** mark both as superseded by ADR 0022, or refresh them to the current wizard.

### Semantic Kernel held at 1.78.0 (2026-10-21)

- **Blocker:** SK 1.79 and later require `Microsoft.Extensions.VectorData.Abstractions` >= 10.5.2. `Microsoft.SemanticKernel.Connectors.SqliteVec` 1.74.0-preview, the last version published, is built against 10.1.0. On 10.5.2 `ChunkRecord`'s `VectorStoreVector(Dimensions: ...)` no longer compiles, and after that is fixed, search throws `MissingMethodException` on `VectorSearchOptions.get_OldFilter()` (8 failures in `Scout.Knowledge.Tests`).
- **Unblocks when:** a SqliteVec build targets VectorData >= 10.5, or the vector store in [[pages/services/knowledge]] moves to another connector.
- **Affects:** the whole SK family pinned in `Directory.Packages.props`, per [[pages/decisions/0012-llm-communication-through-semantic-kernel]].

### `electron-builder-squirrel-windows` peer mismatch (2026-10-21)

- **Blocker:** none. `electron-builder` is on 26.18.0, while its unpinned peer `electron-builder-squirrel-windows` stays at 26.8.1 in `ui/bun.lock`, and `bun install` warns about it. The peer is unused while the Windows target in `ui/build/electron-builder.yml:98` stays `nsis`.
- **Fix:** add the peer at the matching version only if a Squirrel target is ever configured.

### Adapter stop has no fault isolation (2026-10-21)

- **Blocker:** none. `ConversationWorker.StopAsync` stops adapters in sequence with no per-adapter `try`/`catch` (`src/Scout.Host.Console/ConversationWorker.cs:69`), so a fault from one adapter's stop (now possible from Slack's awaited `DisconnectAsync`) leaves the adapters after it running until the process exits.
- **Fix:** isolate and log each adapter's stop in the worker, with a test where the first adapter's stop throws and the second is still stopped. A related edge: `SlackChatAdapter.DisposeAsync` calls `StopAsync(CancellationToken.None)`, so a hung disconnect blocks disposal when the host never called `StopAsync`. See [[pages/history/adapter-slack]].

### Synchronous `safeStorage` calls deprecated in Electron 45 (2026-10-21)

- **Blocker:** removal in Electron 46. `ui/packages/main/src/vault/safe-storage-vault.ts` uses the synchronous `safeStorage` encryption API, deprecated in 45.
- **Fix:** move to the async API before taking Electron 46.

### TypeScript 7 deferred (2026-10-21)

- **Blocker:** `typescript-eslint` 8.72 and `@typescript-eslint/typescript-estree` peer `typescript >=4.8.4 <6.1.0`; `@angular/compiler-cli` and `@angular/build` 22.2 peer `>=6.0 <6.1`.
- **Unblocks when:** both widen their TypeScript peer ranges.

### Packaging and native assets only exercised on Linux (2026-10-21)

- **Blocker:** none. Electron 45 packaging (`dist`) and the SQLitePCLRaw 3 native assets for Windows and macOS have not been run; the first run is the `ui-ci.yml` package step on the three-OS matrix.
- **Fix:** watch that CI run, then a packaged smoke run before a release.

### `dotnet format` check disabled in CI (2026-10-22)

- **Blocker:** `dotnet format --verify-no-changes --no-restore` times out on the MSBuild build-host pipe with SDK 10.0.106; the step is commented out in `.github/workflows/ci.yml`. Code style is still enforced at compile time by `EnforceCodeStyleInBuild` and `TreatWarningsAsErrors`. The GitHub issue that tracked this was deleted on 2026-10-22; this entry replaces it.
- **Unblocks when:** a later SDK pin in `global.json` runs the command clean; then restore the step with the exact name `Format check`. Rule B runs the same command locally, so the gap is CI-only.

### Control-plane socket directory check is not atomic (2026-10-22)

- **Blocker:** none. Found by the review of the phase 1 spike in [[pages/roadmap/control-plane-client]]. The launcher checks the socket directory with `lstat` and then calls `chmod` (`ui/packages/main/src/control-plane/launcher.ts:75`), leaving a window in which a same-uid principal could swap the directory. The environment half of the original entry was closed on 2026-10-23: the launcher now filters the inherited environment (see [[pages/services/host-control-plane]]).
- **Fix:** open the directory with `O_NOFOLLOW` and `fchmod` the descriptor, with a test that a symlink swapped in after the check is refused.

### Control-plane phase 1 test notes (2026-10-22)

- **Blocker:** none. The wrong-token integration test asserts only the error type; add `not.toContain` of the token on its message. The integration `afterAll` throws a `TypeError` when `beforeAll` fails before launching, which adds noise to an already-reported failure.
- **Fix:** both in phase 2, alongside the codegen change to the same files.

### Cloud LLM cards: what is still open (2026-10-22, updated 2026-10-23)

- **Blocker:** none. [[pages/decisions/0015-cloud-llm-providers-by-api-key]] wired the OpenAI API and Google Gemini cards and marked ChatGPT Plus unsupported, which [[pages/decisions/0018-chatgpt-plan-sign-in]] replaced with Sign in with ChatGPT; [[pages/decisions/0017-provider-connect-disconnect]] closed removing a stored key and checking a key against the provider before saving. Still open: OpenRouter (its factory needs a separate embeddings host and key) and Custom API (endpoint validation). Loose ends from ADR 0017: a legacy `llm.api-key` left while the provider is Ollama is never migrated or shown; after disconnecting the provider in use the ready screen says "No LLM model selected" (with provider `None` that is now the honest state, but the wording asks for a local model rather than a provider); and Use applies the card's default model rather than a stored `llm.cloud-model`.
- **Fix:** one change per item; OpenRouter first, since its .NET factory already exists.

### LM Studio and Custom API cards use generic icons (2026-10-23)

- **Blocker:** no freely licensed LM Studio mark exists in Simple Icons, and Custom API has no brand.
- **Fix:** add the official LM Studio artwork to the `ICONS` map in `ui/packages/renderer/src/app/sources/sources.component.ts` once the owner supplies it, with its licence noted beside the Simple Icons line.

### Read-only guard does not judge tool calls (2026-10-23)

- **Blocker:** none. The TypeSafe guard ([[pages/decisions/0016-jev-read-only-guard]]) judges the latest user message, with up to 12 messages before it, before the model runs; a function the model calls during its turn is not judged. Read-only still rests on no write function existing ([[pages/invariants/no-executive-writes]]).
- **Fix:** an `IAutoFunctionInvocationFilter` on the shared kernel that asks Jev about each function name and its arguments before invocation and terminates the call on a yes, with a test where a write-named function is blocked. Decide fail-open or fail-closed for that path separately.

### Turning a feature on does not reach the chat bot (2026-10-23)

- **Blocker:** the control plane registers `NullFeatureActivator` (`src/Scout.ControlPlane/ControlPlaneServiceCollectionExtensions.cs:70`), whose `CanActivate` is false (`src/Scout.ControlPlane/Features/IFeatureActivator.cs:31`), so `Enable` answers `UNIMPLEMENTED` and the Features screen says turning a feature on is not available yet. Nothing tells `Scout.Host.Console` about a feature's state. GitHub and Kubernetes reach the worker through main-owned connections instead ([[pages/decisions/0019-github-app-device-flow]], [[pages/decisions/0020-kubernetes-from-local-kubeconfig]]), so no catalog card calls `Enable` today.
- **Unblocks when:** an activator exists that the console host honours, decided against [[pages/invariants/no-executive-writes]]; then the second note under the Features grid and the card copy change with it. See [[pages/services/host-control-plane]].

### Feature credentials live in memory and are never validated (2026-10-23)

- **Blocker:** none. The control plane keeps credentials in `InMemoryCredentialStore` (`src/Scout.ControlPlane/ControlPlaneServiceCollectionExtensions.cs:69`), so they are lost when Scout closes, and no host registers an `IFeatureCredentialValidator` (the GitHub and Kubeconfig validators exist only under `src/old/`). Neither catalog entry promises a keyring any more: the desktop GitHub card signs in through the GitHub App ([[pages/decisions/0019-github-app-device-flow]]) and the Kubernetes card reads the kubeconfig on this computer ([[pages/decisions/0020-kubernetes-from-local-kubeconfig]]). The Features screen now says credentials are kept only while Scout is open.
- **Fix:** a keyring-backed `ICredentialStore` and the validators in the current tree, or catalog text that matches the in-memory store; one change in `Scout.ControlPlane`.

### Error codes do not cross Electron IPC for most handlers (2026-10-23)

- **Blocker:** none. Electron forwards only the message of an error thrown in an `ipcMain.handle` handler; the `code` property the handlers attach never reaches the renderer (checked on Electron 41.3.0). The Features handlers now encode the code in the message (`ui/packages/shared/src/errors.ts:260`); every other handler does not, so renderer branches on `err.code` never fire in the running app, for example `ui/packages/renderer/src/app/wizard/llm/llm.component.ts:415`.
- **Fix:** encode in every handler and decode once in `ui/packages/preload/src/build-bridge.ts` with `readIpcError`, so the renderer receives a `ScoutError` with its code; drop the Features-only decode in `features.store.ts` then.

### Raw gRPC text for feature validation errors (2026-10-23)

- **Blocker:** none. The desktop control-plane client maps only `UNAVAILABLE`, `DEADLINE_EXCEEDED`, `UNAUTHENTICATED` and `UNIMPLEMENTED` (`ui/packages/main/src/control-plane/client.ts:420`). `INVALID_ARGUMENT` (a missing credential or setting), `FAILED_PRECONDITION` and `NOT_FOUND` reach the Features card as text like `3 INVALID_ARGUMENT: credentials.pat: ...`.
- **Fix:** typed errors for those codes and card copy per code, with a client test for each.

### Kubernetes pod logs: owner decision (2026-10-23)

- **Blocker:** an owner decision. The Kubernetes plugin has no log function ([[pages/decisions/0020-kubernetes-from-local-kubeconfig]]) because chat output is posted to a shared channel and logs routinely carry credentials and personal data. The retired plugin had `get_pod_logs` with a tail clamp (`KubernetesFunctions.cs` of the first cut, in the history before `93da0d4`).
- **Unblocks when:** the owner decides whether logs may reach a shared channel, and with what redaction or limit; then one GET function and its own consent line on the card.

### Merged multi-file `KUBECONFIG` (2026-10-23)

- **Blocker:** none. The desktop reads only the first `KUBECONFIG` entry (`ui/packages/main/src/kubernetes/kubeconfig.ts:20`, a `ponytail:` line), and the worker gets one path, so contexts defined in later files are not offered.
- **Fix:** list contexts from every entry with kubectl's merge rules, store the file per context, and give the plugin one path per context.

### Kubernetes from an in-cluster service account (2026-10-23)

- **Blocker:** none. When Scout itself runs inside a cluster it could use the pod's service account instead of a kubeconfig; the plugin builds clients only from a kubeconfig file (`src/Scout.Plugin.Kubernetes/KubernetesPlugin.cs:305`).
- **Fix:** an opt-in `InCluster` setting that builds one client from `KubernetesClientConfiguration.InClusterConfig()`, with the same GET-only tests.

### Kubernetes consent covers context names, not the file (2026-10-23)

- **Blocker:** none. The desktop stores only the kubeconfig path and the allowed context names; the worker re-reads the live file each time it builds a client (`src/Scout.Plugin.Kubernetes/KubernetesPlugin.cs:305`), so a context whose user later gains or changes an exec command runs the new command without a new disclosure ([[pages/decisions/0020-kubernetes-from-local-kubeconfig]]).
- **Fix:** store a fingerprint of each allowed context's cluster and user sections with the selection, and have main drop the selection and ask again when it changes.

### The control-plane toggle and credential dialog are unreachable on the desktop (2026-10-23)

- **Blocker:** an owner decision. Both catalog features now show a main-owned connection (`ui/packages/renderer/src/app/features/features.component.ts:183`), so the toggle, the credential dialog and the `Enable` path in `FeaturesComponent` serve no typed feature; their specs reach them only through a cast feature id (`ui/packages/renderer/src/app/features/features.component.spec.ts:66`).
- **Fix:** remove that path and its specs, or keep it for a future feature without a main-owned connection.

### The GitHub card bypasses the control-plane Features service (2026-10-23)

- **Blocker:** no activator. The desktop main process owns the GitHub sign-in and hands the worker a token file (`ui/packages/main/src/worker/build-env.ts:76`); the control plane's GitHub entry, its `pat` credential and its enabled state play no part in it, so the GitHub card shows the main-owned connection as its badge and hides the toggle and credential buttons. The Kubernetes card does the same with the kubeconfig selection ([[pages/decisions/0020-kubernetes-from-local-kubeconfig]]). The card also hides with the Features grid when the control plane is not running, although the sign-in does not need it.
- **Unblocks when:** a real feature activator exists; then decide whether the control plane reports the GitHub connection or the card leaves the control-plane grid.

### An API-key provider does not close the bot to other users (2026-10-23)

- **Blocker:** an owner decision. `SCOUT__ACCESS__REQUIREALLOWLIST` is set for GitHub, Kubernetes and the ChatGPT plan only (`ui/packages/main/src/worker/build-env.ts:88`); with an OpenAI or Gemini key in use and no allowed users, anyone who can message the bot spends the owner's API credit ([[pages/decisions/0021-allowed-users]]).
- **Fix:** add `llm.provider` OpenAI and Gemini to the condition, with the same card warning on those cards, if the owner wants API credit treated as an owner credential.

### Teams allowed-user ids are Bot Framework ids (2026-10-23)

- **Blocker:** none while Teams has no webhook in the shipped host. The Teams translator passes `activity.From.Id` (`src/Scout.Adapter.Teams/TeamsMessageTranslator.cs:124`), a `29:` id Teams does not show to users, not the Microsoft Entra object id; the Settings help says so.
- **Fix:** pass `activity.From.AadObjectId` as `UserId` when the Teams webhook lands, and change the Settings help to "the user's Microsoft Entra object ID"; check that a real `29:` or object id fits the 64-character limit of `ui/packages/main/src/ipc/access.ts:16`.

### Allowed-user ids are not platform-qualified (2026-10-23)

- **Blocker:** none; the desktop app runs Discord only. An id listed for one platform also admits the same string on another (`src/Scout.Conversation/ConversationConductor.cs:330`).
- **Fix:** store and compare `Adapter:UserId`, as [[pages/decisions/0014-per-request-identity-gating]] anticipates, when a second adapter ships in the desktop app.

### The script writer is not told how the bundled voice reads capitals (2026-11-04)

- **Blocker:** none. `PiperSpeech.Spelled` (`src/Scout.Operator/PiperSpeech.cs:125`) writes every run of 2 to 4 capital letters as dotted letters, so the bundled voice spells it; a word-like acronym ("Nasa", "Pib") is read as a word only when it arrives capitalised, and nothing in the script prompt says so.
- **Fix:** tell the script writer to write word-like acronyms capitalised ("Nasa", "Pib") and spelled ones in capitals ("ETF", "IPO").

### The desktop composes the presenter prompt with its own copy of the worker's strings (2026-11-05)

- **Blocker:** none. The Presenter block prefills the prompt from `PRESENTER_STYLE`, `PRESENTER_SAFETY` and its own composition (`ui/packages/renderer/src/app/production/production.component.ts:95`), copies of `PresenterStage.Style`, `VisualGenerator.NoRealPerson` and `PresenterStage.ComposedPrompt`; the review of phase 8c-B caught the two style strings apart (the desktop said "plain background", the worker needs the green one the chroma key removes) and made them equal, but nothing keeps them equal.
- **Fix:** carry the composed prompt and the safety tail on the wire (a `Readiness` or `Project` field the worker fills), so the desktop shows what the worker uses and composes nothing itself.

### The desktop validates presenters.json with stricter rules than the worker (2026-11-05)

- **Blocker:** none; the shipped manifest (`ui/packages/main/resources/presenters/presenters.json`) satisfies both. `presentersFolder` (`ui/packages/main/src/worker/resolve-binary.ts`) accepts only a bare array whose entries name a `file`, while `BuiltInPresenters` (`src/Scout.Operator/BuiltInPresenters.cs`) also reads an object with `presenters` and defaults the file to `<id>.png`; a manifest the worker would read can be withheld from it by the desktop with an empty `SCOUT__OPERATOR__PRESENTERSPATH`.
- **Fix:** one validator: the desktop checks the folder exists and leaves the manifest to the worker, which keeps the plain-basename rule.

### Three WorkerGate chat rpcs and one Operator rpc have no caller (2026-11-05)

- **Blocker:** none. `ChatEditScenePicturePrompt` and `ChatRegenerateScenePicture` (`src/Scout.ControlPlane.Contracts/protos/worker.proto`) exist for the owner's picture edits from the chat, but the plugin offers no function for them; `ListBuiltInPresenters` (`operator.proto`) answers the readiness list the desktop already reads. Each is tested on the control plane and reachable only under the grant.
- **Fix:** add `youtube_edit_scene_picture` and `youtube_regenerate_scene_picture` to the plugin under the owner-identity and state-read rules of phase 8d, or reserve the three rpc names in the proto and drop them.

### The face models stay resident after the first readiness round (2026-11-05)

- **Blocker:** none, unmeasured. `OnnxRunner` keeps one session per model for the life of the worker (`src/Scout.Operator/FaceMouth.cs`), and the readiness probe loads both at the first round whether or not a presenter job ever runs; the optimizer estimated tens of megabytes of RSS.
- **Fix:** measure the worker's RSS before and after the first round; if it matters, probe with `File.Exists` only and load the sessions at the first job, dropping them after an idle hour.
