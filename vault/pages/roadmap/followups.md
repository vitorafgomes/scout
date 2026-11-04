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

- **Blocker:** no signing identities. The release workflow packages with `CSC_IDENTITY_AUTO_DISCOVERY` false and the mac config sets `identity: null` (`ui/build/electron-builder.yml:77`), so macOS Gatekeeper refuses the dmg until the user allows it in System Settings, and Windows SmartScreen warns on the installer. The site and the README say so ([[pages/decisions/0029-project-site-and-releases]]).
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

- **Blocker:** the owner's run of the AppImage. On Linux under Wayland with XWayland, Scout relaunches once with `--ozone-platform=x11` (`ui/packages/main/src/shell/x11-relaunch.ts:12`, `ui/packages/main/src/main.ts:119`) so the floating button and the quick-ask window are placed in the chosen corner and stay on top through XWayland. The corner and display choice are measured to hold (below); what stays open is only whether the bubble stays above native Wayland windows, visible only on screen. The second start costs one extra process launch on every Wayland start.
- **Fix:** none expected; what remains open is the owner confirming on screen that the bubble stays above native Wayland windows. If it does not, record it under ADR 0028.
- **Measured 2026-10-31 with Electron 45 under `--ozone-platform=x11`:** every window COSMIC's X window manager manages, whatever its type, is placed where COSMIC chooses, ignores `setPosition` and `setBounds` before and after `show`, reports not always-on-top and gets a server-side frame. A window created with `focusable: false`, shown, then made focusable and focused stays unmanaged, keeps its position and always-on-top, and `document.hasFocus()` reported focus to its page, but that was not real X keyboard focus (see the last bullet).
- **Measured 2026-10-31 on the owner's COSMIC session** (AppImage started with `--ozone-platform=x11`, read with `xwininfo` and `xprop` on `DISPLAY=:1`): the bubble window was 64x64 at +1840+1190, the bottom-right corner of the 1920x1080 laptop display (offset +0+190) minus the 16 px margin, so the corner and the display choice hold. COSMIC's X window manager reports itself as "Smithay X WM" and lists `_NET_WM_STATE_ABOVE` and `_NET_WM_STATE_SKIP_TASKBAR` in `_NET_SUPPORTED`. The bubble carries no `WM_STATE` and is absent from `_NET_CLIENT_LIST_STACKING`: with `focusable: false` Electron creates it as a window the X window manager does not manage, which the compositor draws above managed windows. Whether that holds above native Wayland windows is only visible on screen.
- **Measured 2026-10-31 on the owner's COSMIC session:** an unmanaged window never receives keyboard focus there: `_NET_ACTIVE_WINDOW` on the root window stays 0 while it is shown, and Electron's `focus()` on X11 only asks the window manager, which ignores a window it does not manage. The owner could not type in the quick-ask window or close it. The quick-ask window is now managed on Linux (`ui/packages/main/src/quick-ask/quick-ask.ts:352`), so on COSMIC it loses the corner and gets a frame, while the bubble keeps the corner. Keeping the corner and the keyboard there needs a window the compositor positions (a Wayland layer-shell surface or a COSMIC-specific protocol), which Electron does not offer.

### Voice transcript shown inside the reply, not as the question (2026-10-31)

- **Blocker:** no worker event carries the transcript. The owner's request is deferred, not done: a voice turn's question is "(voice message)" (`ui/packages/main/src/quick-ask/quick-ask.ts:62`), and what Scout heard reaches the owner only as the reply's opening `> (voice) ` quote, which the worker writes once the reply starts. The owner asked to see what was heard so they can check it ("deveria mostrar o que falei, para que eu veja se esta correto").
- **Fix:** a `heard` event from the local adapter, carrying the transcript before the reply, that main sets as the turn's question; the reply then drops the quote for local turns. It touches the worker's local protocol and main's turn handling, with a test on each side.

### Floating button cannot be dragged (2026-10-31)

- **Blocker:** none. The floating button sits only in one of four corners, 16 px from the edges of the chosen display's work area (`ui/packages/main/src/quick-ask/placement.ts:11`); the owner cannot move it out of the way of something in that corner ([[pages/decisions/0028-local-quick-ask-assistant]]).
- **Fix:** pointer-drag in `FloatingButtonComponent` through an IPC that moves the bubble window from main, telling a drag from a click, and remember the offset per display under `quick-ask.bubble`; a click still opens quick ask. Wayland stays compositor-placed.

### Floating button does not follow display changes (2026-10-31)

- **Blocker:** none. The bubble is placed when it is created, when its settings change and each time the quick-ask window hides (`ui/packages/main/src/quick-ask/quick-ask.ts:287`); unplugging a display, or a resolution or scale change, can leave it off screen or away from its corner until then.
- **Fix:** reposition it on `screen`'s `display-metrics-changed` and `display-removed` events, removing the listeners on dispose, with a test that emits each event on the fake screen.

### Clear on the Chat screen keeps the worker's local memory (2026-10-30)

- **Blocker:** no channel for it. Clear empties only the conversation main holds for the quick-ask window and the Chat screen (`ui/packages/main/src/quick-ask/quick-ask.ts:512`); the worker keeps the `local` channel's turns in `IConversationMemoryStore` until they expire or the worker restarts, so a question after Clear can still draw on the cleared conversation ([[pages/decisions/0028-local-quick-ask-assistant]]).
- **Fix:** let Clear also reset the worker's memory for the local channel, for example a `reset` request on the quick-ask stdin protocol that the local adapter turns into a memory reset; it touches only Scout's own state.

### Provisioning pages still describe the old Discord flow (2026-10-28)

- **Blocker:** none. `pages/architecture/electron-provisioning-ui/per-platform-feasibility.md:52` to `:54` and `pages/architecture/desktop-ui-design-system/5-screens/screen-5-discord-wizard-step-3-oauth2-install.md` still describe the manual intent toggle and the loopback OAuth install, which [[pages/decisions/0022-discord-setup-from-the-bot-token]] replaced with the bot-token setup and a plain `scope=bot` invite.
- **Fix:** mark both as superseded by ADR 0022, or refresh them to the current wizard.

### Semantic Kernel held at 1.78.0 (2026-10-21)

- **Blocker:** SK 1.79 and later require `Microsoft.Extensions.VectorData.Abstractions` >= 10.5.2. `Microsoft.SemanticKernel.Connectors.SqliteVec` 1.74.0-preview, the last version published, is built against 10.1.0. On 10.5.2 `ChunkRecord`'s `VectorStoreVector(Dimensions: ...)` no longer compiles, and after that is fixed, search throws `MissingMethodException` on `VectorSearchOptions.get_OldFilter()` (8 failures in `Scout.Knowledge.Tests`).
- **Unblocks when:** a SqliteVec build targets VectorData >= 10.5, or the vector store in [[pages/services/knowledge]] moves to another connector.
- **Affects:** the whole SK family pinned in `Directory.Packages.props`, per [[pages/decisions/0012-llm-communication-through-semantic-kernel]].

### `electron-builder-squirrel-windows` peer mismatch (2026-10-21)

- **Blocker:** none. `electron-builder` is on 26.18.0, while its unpinned peer `electron-builder-squirrel-windows` stays at 26.8.1 in `ui/bun.lock`, and `bun install` warns about it. The peer is unused while the Windows target in `ui/build/electron-builder.yml:93` stays `nsis`.
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

- **Blocker:** none. The desktop control-plane client maps only `UNAVAILABLE`, `DEADLINE_EXCEEDED`, `UNAUTHENTICATED` and `UNIMPLEMENTED` (`ui/packages/main/src/control-plane/client.ts:390`). `INVALID_ARGUMENT` (a missing credential or setting), `FAILED_PRECONDITION` and `NOT_FOUND` reach the Features card as text like `3 INVALID_ARGUMENT: credentials.pat: ...`.
- **Fix:** typed errors for those codes and card copy per code, with a client test for each.

### Kubernetes pod logs: owner decision (2026-10-23)

- **Blocker:** an owner decision. The Kubernetes plugin has no log function ([[pages/decisions/0020-kubernetes-from-local-kubeconfig]]) because chat output is posted to a shared channel and logs routinely carry credentials and personal data. The retired plugin had `get_pod_logs` with a tail clamp (`src/old/Scout.Plugin.Kubernetes/KubernetesFunctions.cs:125`).
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

- **Blocker:** an owner decision. Both catalog features now show a main-owned connection (`ui/packages/renderer/src/app/features/features.component.ts:180`), so the toggle, the credential dialog and the `Enable` path in `FeaturesComponent` serve no typed feature; their specs reach them only through a cast feature id (`ui/packages/renderer/src/app/features/features.component.spec.ts:65`).
- **Fix:** remove that path and its specs, or keep it for a future feature without a main-owned connection.

### The GitHub card bypasses the control-plane Features service (2026-10-23)

- **Blocker:** no activator. The desktop main process owns the GitHub sign-in and hands the worker a token file (`ui/packages/main/src/worker/build-env.ts:68`); the control plane's GitHub entry, its `pat` credential and its enabled state play no part in it, so the GitHub card shows the main-owned connection as its badge and hides the toggle and credential buttons. The Kubernetes card does the same with the kubeconfig selection ([[pages/decisions/0020-kubernetes-from-local-kubeconfig]]). The card also hides with the Features grid when the control plane is not running, although the sign-in does not need it.
- **Unblocks when:** a real feature activator exists; then decide whether the control plane reports the GitHub connection or the card leaves the control-plane grid.

### An API-key provider does not close the bot to other users (2026-10-23)

- **Blocker:** an owner decision. `SCOUT__ACCESS__REQUIREALLOWLIST` is set for GitHub, Kubernetes and the ChatGPT plan only (`ui/packages/main/src/worker/build-env.ts:80`); with an OpenAI or Gemini key in use and no allowed users, anyone who can message the bot spends the owner's API credit ([[pages/decisions/0021-allowed-users]]).
- **Fix:** add `llm.provider` OpenAI and Gemini to the condition, with the same card warning on those cards, if the owner wants API credit treated as an owner credential.

### Teams allowed-user ids are Bot Framework ids (2026-10-23)

- **Blocker:** none while Teams has no webhook in the shipped host. The Teams translator passes `activity.From.Id` (`src/Scout.Adapter.Teams/TeamsMessageTranslator.cs:122`), a `29:` id Teams does not show to users, not the Microsoft Entra object id; the Settings help says so.
- **Fix:** pass `activity.From.AadObjectId` as `UserId` when the Teams webhook lands, and change the Settings help to "the user's Microsoft Entra object ID"; check that a real `29:` or object id fits the 64-character limit of `ui/packages/main/src/ipc/access.ts:16`.

### Allowed-user ids are not platform-qualified (2026-10-23)

- **Blocker:** none; the desktop app runs Discord only. An id listed for one platform also admits the same string on another (`src/Scout.Conversation/ConversationConductor.cs:331`).
- **Fix:** store and compare `Adapter:UserId`, as [[pages/decisions/0014-per-request-identity-gating]] anticipates, when a second adapter ships in the desktop app.
