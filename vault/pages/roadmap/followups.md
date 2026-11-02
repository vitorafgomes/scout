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

### X11 always-on-top unverified on COSMIC (2026-10-31)

- **Blocker:** the owner's run of the AppImage. On Linux under Wayland with XWayland, Scout relaunches once with `--ozone-platform=x11` (`ui/packages/main/src/shell/x11-relaunch.ts:12`, `ui/packages/main/src/main.ts:87`) so the floating button and the quick-ask window are placed in the chosen corner and stay on top through XWayland. The corner and display choice are measured to hold (below); what stays open is only whether the bubble stays above native Wayland windows, visible only on screen. The second start costs one extra process launch on every Wayland start.
- **Fix:** none expected; what remains open is the owner confirming on screen that the bubble stays above native Wayland windows. If it does not, record it under ADR 0028.
- **Measured 2026-10-31 with Electron 45 under `--ozone-platform=x11`:** every window COSMIC's X window manager manages, whatever its type, is placed where COSMIC chooses, ignores `setPosition` and `setBounds` before and after `show`, reports not always-on-top and gets a server-side frame. A window created with `focusable: false`, shown, then made focusable and focused stays unmanaged, keeps its position and always-on-top, and `document.hasFocus()` reported focus to its page, but that was not real X keyboard focus (see the last bullet).
- **Measured 2026-10-31 on the owner's COSMIC session** (AppImage started with `--ozone-platform=x11`, read with `xwininfo` and `xprop` on `DISPLAY=:1`): the bubble window was 64x64 at +1840+1190, the bottom-right corner of the 1920x1080 laptop display (offset +0+190) minus the 16 px margin, so the corner and the display choice hold. COSMIC's X window manager reports itself as "Smithay X WM" and lists `_NET_WM_STATE_ABOVE` and `_NET_WM_STATE_SKIP_TASKBAR` in `_NET_SUPPORTED`. The bubble carries no `WM_STATE` and is absent from `_NET_CLIENT_LIST_STACKING`: with `focusable: false` Electron creates it as a window the X window manager does not manage, which the compositor draws above managed windows. Whether that holds above native Wayland windows is only visible on screen.
- **Measured 2026-10-31 on the owner's COSMIC session:** an unmanaged window never receives keyboard focus there: `_NET_ACTIVE_WINDOW` on the root window stays 0 while it is shown, and Electron's `focus()` on X11 only asks the window manager, which ignores a window it does not manage. The owner could not type in the quick-ask window or close it. The quick-ask window is now managed on Linux (`ui/packages/main/src/quick-ask/quick-ask.ts:343`), so on COSMIC it loses the corner and gets a frame, while the bubble keeps the corner. Keeping the corner and the keyboard there needs a window the compositor positions (a Wayland layer-shell surface or a COSMIC-specific protocol), which Electron does not offer.

### Voice transcript shown inside the reply, not as the question (2026-10-31)

- **Blocker:** no worker event carries the transcript. The owner's request is deferred, not done: a voice turn's question is "(voice message)" (`ui/packages/main/src/quick-ask/quick-ask.ts:61`), and what Scout heard reaches the owner only as the reply's opening `> (voice) ` quote, which the worker writes once the reply starts. The owner asked to see what was heard so they can check it ("deveria mostrar o que falei, para que eu veja se esta correto").
- **Fix:** a `heard` event from the local adapter, carrying the transcript before the reply, that main sets as the turn's question; the reply then drops the quote for local turns. It touches the worker's local protocol and main's turn handling, with a test on each side.

### Floating button cannot be dragged (2026-10-31)

- **Blocker:** none. The floating button sits only in one of four corners, 16 px from the edges of the chosen display's work area (`ui/packages/main/src/quick-ask/placement.ts:11`); the owner cannot move it out of the way of something in that corner ([[pages/decisions/0028-local-quick-ask-assistant]]).
- **Fix:** pointer-drag in `FloatingButtonComponent` through an IPC that moves the bubble window from main, telling a drag from a click, and remember the offset per display under `quick-ask.bubble`; a click still opens quick ask. Wayland stays compositor-placed.

### Floating button does not follow display changes (2026-10-31)

- **Blocker:** none. The bubble is placed when it is created, when its settings change and each time the quick-ask window hides (`ui/packages/main/src/quick-ask/quick-ask.ts:279`); unplugging a display, or a resolution or scale change, can leave it off screen or away from its corner until then.
- **Fix:** reposition it on `screen`'s `display-metrics-changed` and `display-removed` events, removing the listeners on dispose, with a test that emits each event on the fake screen.

### Clear on the Chat screen keeps the worker's local memory (2026-10-30)

- **Blocker:** no channel for it. Clear empties only the conversation main holds for the quick-ask window and the Chat screen (`ui/packages/main/src/quick-ask/quick-ask.ts:497`); the worker keeps the `local` channel's turns in `IConversationMemoryStore` until they expire or the worker restarts, so a question after Clear can still draw on the cleared conversation ([[pages/decisions/0028-local-quick-ask-assistant]]).
- **Fix:** let Clear also reset the worker's memory for the local channel, for example a `reset` request on the quick-ask stdin protocol that the local adapter turns into a memory reset; it touches only Scout's own state.

### Provisioning pages still describe the old Discord flow (2026-10-28)

- **Blocker:** none. `pages/architecture/electron-provisioning-ui/per-platform-feasibility.md:52` to `:54` and `pages/architecture/desktop-ui-design-system/5-screens/screen-5-discord-wizard-step-3-oauth2-install.md` still describe the manual intent toggle and the loopback OAuth install, which [[pages/decisions/0022-discord-setup-from-the-bot-token]] replaced with the bot-token setup and a plain `scope=bot` invite.
- **Fix:** mark both as superseded by ADR 0022, or refresh them to the current wizard.

### Semantic Kernel held at 1.78.0 (2026-10-21)

- **Blocker:** SK 1.79 and later require `Microsoft.Extensions.VectorData.Abstractions` >= 10.5.2. `Microsoft.SemanticKernel.Connectors.SqliteVec` 1.74.0-preview, the last version published, is built against 10.1.0. On 10.5.2 `ChunkRecord`'s `VectorStoreVector(Dimensions: ...)` no longer compiles, and after that is fixed, search throws `MissingMethodException` on `VectorSearchOptions.get_OldFilter()` (8 failures in `Scout.Knowledge.Tests`).
- **Unblocks when:** a SqliteVec build targets VectorData >= 10.5, or the vector store in [[pages/services/knowledge]] moves to another connector.
- **Affects:** the whole SK family pinned in `Directory.Packages.props`, per [[pages/decisions/0012-llm-communication-through-semantic-kernel]].

### `electron-builder-squirrel-windows` peer mismatch (2026-10-21)

- **Blocker:** none. `electron-builder` is on 26.18.0, while its unpinned peer `electron-builder-squirrel-windows` stays at 26.8.1 in `ui/bun.lock`, and `bun install` warns about it. The peer is unused while the Windows target in `ui/build/electron-builder.yml:57` stays `nsis`.
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

- **Blocker:** none. Found by the review of the phase 1 spike in [[pages/roadmap/control-plane-client]]. The launcher checks the socket directory with `lstat` and then calls `chmod` (`ui/packages/main/src/control-plane/launcher.ts:65`), leaving a window in which a same-uid principal could swap the directory. The environment half of the original entry was closed on 2026-10-23: the launcher now filters the inherited environment (see [[pages/services/host-control-plane]]).
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

- **Blocker:** the control plane registers `NullFeatureActivator` (`src/Scout.ControlPlane/ControlPlaneServiceCollectionExtensions.cs:69`), whose `CanActivate` is false (`src/Scout.ControlPlane/Features/IFeatureActivator.cs:31`), so `Enable` answers `UNIMPLEMENTED` and the Features screen says turning a feature on is not available yet. Nothing tells `Scout.Host.Console` about a feature's state. GitHub and Kubernetes reach the worker through main-owned connections instead ([[pages/decisions/0019-github-app-device-flow]], [[pages/decisions/0020-kubernetes-from-local-kubeconfig]]), so no catalog card calls `Enable` today.
- **Unblocks when:** an activator exists that the console host honours, decided against [[pages/invariants/no-executive-writes]]; then the second note under the Features grid and the card copy change with it. See [[pages/services/host-control-plane]].

### Feature credentials live in memory and are never validated (2026-10-23)

- **Blocker:** none. The control plane keeps credentials in `InMemoryCredentialStore` (`src/Scout.ControlPlane/ControlPlaneServiceCollectionExtensions.cs:68`), so they are lost when Scout closes, and no host registers an `IFeatureCredentialValidator` (the GitHub and Kubeconfig validators exist only under `src/old/`). Neither catalog entry promises a keyring any more: the desktop GitHub card signs in through the GitHub App ([[pages/decisions/0019-github-app-device-flow]]) and the Kubernetes card reads the kubeconfig on this computer ([[pages/decisions/0020-kubernetes-from-local-kubeconfig]]). The Features screen now says credentials are kept only while Scout is open.
- **Fix:** a keyring-backed `ICredentialStore` and the validators in the current tree, or catalog text that matches the in-memory store; one change in `Scout.ControlPlane`.

### Error codes do not cross Electron IPC for most handlers (2026-10-23)

- **Blocker:** none. Electron forwards only the message of an error thrown in an `ipcMain.handle` handler; the `code` property the handlers attach never reaches the renderer (checked on Electron 41.3.0). The Features handlers now encode the code in the message (`ui/packages/shared/src/errors.ts:232`); every other handler does not, so renderer branches on `err.code` never fire in the running app, for example `ui/packages/renderer/src/app/wizard/llm/llm.component.ts:415`.
- **Fix:** encode in every handler and decode once in `ui/packages/preload/src/build-bridge.ts` with `readIpcError`, so the renderer receives a `ScoutError` with its code; drop the Features-only decode in `features.store.ts` then.

### Raw gRPC text for feature validation errors (2026-10-23)

- **Blocker:** none. The desktop control-plane client maps only `UNAVAILABLE`, `DEADLINE_EXCEEDED`, `UNAUTHENTICATED` and `UNIMPLEMENTED` (`ui/packages/main/src/control-plane/client.ts:95`). `INVALID_ARGUMENT` (a missing credential or setting), `FAILED_PRECONDITION` and `NOT_FOUND` reach the Features card as text like `3 INVALID_ARGUMENT: credentials.pat: ...`.
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

- **Blocker:** an owner decision. Both catalog features now show a main-owned connection (`ui/packages/renderer/src/app/features/features.component.ts:150`), so the toggle, the credential dialog and the `Enable` path in `FeaturesComponent` serve no typed feature; their specs reach them only through a cast feature id (`ui/packages/renderer/src/app/features/features.component.spec.ts:56`).
- **Fix:** remove that path and its specs, or keep it for a future feature without a main-owned connection.

### The GitHub card bypasses the control-plane Features service (2026-10-23)

- **Blocker:** no activator. The desktop main process owns the GitHub sign-in and hands the worker a token file (`ui/packages/main/src/worker/build-env.ts:56`); the control plane's GitHub entry, its `pat` credential and its enabled state play no part in it, so the GitHub card shows the main-owned connection as its badge and hides the toggle and credential buttons. The Kubernetes card does the same with the kubeconfig selection ([[pages/decisions/0020-kubernetes-from-local-kubeconfig]]). The card also hides with the Features grid when the control plane is not running, although the sign-in does not need it.
- **Unblocks when:** a real feature activator exists; then decide whether the control plane reports the GitHub connection or the card leaves the control-plane grid.

### An API-key provider does not close the bot to other users (2026-10-23)

- **Blocker:** an owner decision. `SCOUT__ACCESS__REQUIREALLOWLIST` is set for GitHub, Kubernetes and the ChatGPT plan only (`ui/packages/main/src/worker/build-env.ts:68`); with an OpenAI or Gemini key in use and no allowed users, anyone who can message the bot spends the owner's API credit ([[pages/decisions/0021-allowed-users]]).
- **Fix:** add `llm.provider` OpenAI and Gemini to the condition, with the same card warning on those cards, if the owner wants API credit treated as an owner credential.

### Teams allowed-user ids are Bot Framework ids (2026-10-23)

- **Blocker:** none while Teams has no webhook in the shipped host. The Teams translator passes `activity.From.Id` (`src/Scout.Adapter.Teams/TeamsMessageTranslator.cs:122`), a `29:` id Teams does not show to users, not the Microsoft Entra object id; the Settings help says so.
- **Fix:** pass `activity.From.AadObjectId` as `UserId` when the Teams webhook lands, and change the Settings help to "the user's Microsoft Entra object ID"; check that a real `29:` or object id fits the 64-character limit of `ui/packages/main/src/ipc/access.ts:16`.

### Allowed-user ids are not platform-qualified (2026-10-23)

- **Blocker:** none; the desktop app runs Discord only. An id listed for one platform also admits the same string on another (`src/Scout.Conversation/ConversationConductor.cs:331`).
- **Fix:** store and compare `Adapter:UserId`, as [[pages/decisions/0014-per-request-identity-gating]] anticipates, when a second adapter ships in the desktop app.
