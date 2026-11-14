---
title: "ADR 0053 - Electron fuses and the IPC sender check"
version: "1.5"
date: 2026-11-08
changes: "v1.5 (2026-11-11): bringing the Scout window forward answers only the operator window (SHOW_MAIN_GUARDS)."
page_type: adr
status: accepted
description: "The packaged desktop ships with the fuses that make it a Node runtime off and cookie encryption, asar integrity and asar-only loading on, and keeps the file protocol privileges its renderer loads with; the write-granting channels answer only the main window's own page, and the action decisions only the main, quick-ask and operator windows'."
supersedes: ""
superseded_by: ""
source:
  - chat
  - pages/roadmap/security-hardening.md
  - pages/decisions/0032-per-feature-write-permissions.md
  - ui/build/electron-builder.yml
  - ui/packages/main/src/ipc/features.ts
  - ui/e2e/smoke/packaged-app.spec.ts
reliability: high
---

# ADR 0053 - Electron fuses and the IPC sender check

> The packaged desktop cannot be turned into a Node runtime, only the main window grants, and only the windows that show approval cards decide.

## Status

- [x] Proposed
- [x] Accepted

Built: 2026-11-08, phase S1 of [[pages/roadmap/security-hardening]].

## Context

Scout Desktop is an Electron app whose main process holds the vault key, the control-plane session token and every write grant. Two gaps were open before this decision.

- **The binary.** Electron's prebuilt binary has fuses, flags in a fuse wire inside the executable that the binary reads at startup. By default the fuses let anyone who can start the app turn it into a plain Node runtime (`ELECTRON_RUN_AS_NODE=1`), pass Node options through `NODE_OPTIONS`, or open an inspector on main with `--inspect`. Any of these runs code with main's access to the keyring entry and the control-plane socket, outside the app's own code. No fuse was configured.
- **The IPC sender.** [[pages/decisions/0032-per-feature-write-permissions]] says only the main window grants. Since its amendment of 2026-11-08 main refused a grant from another window, but only for `scout:features.writeGrant` with `granted: true` and `scout:features.enable` with `confirmWrites: true`, and only by window, not by frame. The decision channels `scout:actions.approve` and `scout:actions.reject` answered any sender. Every Scout window loads the same preload, so the floating button and the operator window expose the full bridge. A renderer that runs injected script, or a sub-frame, could reach those channels.

## Decision

- **The fuses.** `electronFuses` in `ui/build/electron-builder.yml:40` is electron-builder's own fuse support, which flips the wire right before signing. No package is added: electron-builder 26.18 already depends on `@electron/fuses`.
  - `runAsNode: false`: `ELECTRON_RUN_AS_NODE` is ignored. Main never forks itself: the worker, the control plane and the media tools are separate executables started with `spawn`.
  - `enableNodeOptionsEnvironmentVariable: false`: `NODE_OPTIONS` and `NODE_EXTRA_CA_CERTS` are ignored.
  - `enableNodeCliInspectArguments: false`: `--inspect` and its variants are ignored, and `SIGUSR1` does not open the inspector.
  - `enableCookieEncryption: true`: the cookie store on disk is encrypted with the OS key, as Chrome does. It is a one-way change: turning it off later corrupts the store.
  - `enableEmbeddedAsarIntegrityValidation: true`: on macOS and Windows main checks `app.asar` against the hash electron-builder embeds (the `ElectronAsarIntegrity` plist entry on macOS, an `INTEGRITY` resource on Windows). Linux has no such check, so there the fuse is set and does nothing.
  - `onlyLoadAppFromAsar: true`: main loads the app only from `app.asar`, never from an `app` folder placed beside it. Combined with the integrity check, no unchecked code is loaded.
  - `grantFileProtocolExtraPrivileges: true`, the default, kept on purpose. Every window loads the renderer with `loadFile` from `file://` inside `app.asar`. With the fuse off, the packaged app's windows stayed blank: main logged `Failed to load URL: file:///.../resources/app.asar/packages/renderer/dist/browser/index.html with error: ERR_FILE_NOT_FOUND`, and the four smoke tests that launch the app failed waiting for the main window. The same extracted app with only this fuse turned back on showed the welcome screen. Turning it off needs the renderer served from a custom protocol first, a follow-up on [[pages/roadmap/security-hardening]].
  - `resetAdHocDarwinSignature: true`: the macOS build is unsigned (`mac.identity: null`), so electron-builder does not sign after the fuses flip. Flipping invalidates the prebuilt binary's ad-hoc signature, which Apple Silicon requires to start the app, so the signature is reset.
  - `loadBrowserProcessSpecificV8Snapshot` keeps its default, off; Scout has no browser-specific snapshot.
- **The sender rule.** One check, `senderCheck` (`ui/packages/main/src/ipc/sender-check.ts:42`), classifies the sender of each guarded call before any other work; the features handlers and the operator handlers both use it:
  - The kinds are `main`, `quick-ask`, `floating-button`, `operator`, `unknown`, `sub-frame` and `other-page`. Main names the window by the identity of its `webContents` (`windowOf`, `ui/packages/main/src/main.ts:426`); the quick-ask module knows its own two windows (`ui/packages/main/src/quick-ask/quick-ask.ts:1784`; since 2026-11-10 the floating button's card window, which is neither, is `unknown`) and the operator module its own one (`ui/packages/main/src/operator/operator-window.ts:127`). Any other `webContents` is `unknown`.
  - A call from one of Scout's windows counts as that window only when `event.senderFrame` is the window's main frame (same `frameTreeNodeId`) and its URL is Scout's own page, checked by `appPageCheck` (`ui/packages/main/src/quick-ask/quick-ask.ts:267`, the check the microphone and screen-capture permission handlers already use). A sub-frame, or a missing frame, is `sub-frame`. A main frame that is not Scout's page is `other-page`.
  - **The grant family answers only the main window:** `scout:features.writeGrant` (grant and revoke), `scout:features.enable` (with or without `confirmWrites`), `scout:features.disable` and `scout:features.deleteCredentials` (`ui/packages/main/src/ipc/features.ts:138`, `:152`, `:173`, `:187`). The refusal keeps the code and message of 2026-11-08: `features/not-main-window`, "Writes can be allowed only from the Scout window."
  - **The decision family answers the windows that show approval cards:** `scout:actions.approve` and `scout:actions.reject` (`ui/packages/main/src/ipc/features.ts:230`) answer the main window, whose Chat screen and Approvals screen show the cards, the quick-ask window, whose approval strip shows them, and the operator window, whose Approvals page is the same Approvals screen filtered to YouTube (route `operator/approvals`, `ui/packages/renderer/src/app/app.routes.ts:116`). The floating button shows none. The refusal is `actions/not-approval-window`, "Actions can be approved or rejected only from the Scout, quick-ask or operator window."
  - A refusal is logged at warn as `ipc: call refused for its sender` with the channel and the sender kind only (`ui/packages/main/src/ipc/sender-check.ts:55`), never the request. It is thrown before the handler's own `try`, so the handler's failure log, which carries the feature id, never sees it.
  - **Credentials are entered only in the main window:** `scout:features.setCredentials` and `scout:features.setup` (`ui/packages/main/src/ipc/features.ts:110`, `:124`) answer only the main window. Every credential the owner types or signs in with reaches main from the main window alone: its wizard (the Discord token and the Slack app), Settings (the chat connections and the allowed users), AI Sources (the provider keys, the ChatGPT plan and the usage admin keys) and the Features screen (the feature credentials and the GitHub, YouTube and Kubernetes cards); the quick-ask, floating-button and operator windows only read the vault ([[pages/invariants/secrets-never-in-source]]). The refusal is `features/not-main-window` with "Credentials can be entered only in the Scout window.", and the log line carries no credential.
  - **Credentials and access outside `scout:features.*` answer only the main window:** the twenty-three channels that store or delete a credential, or grant or revoke access (`CREDENTIAL_CHANNELS`, `ui/packages/main/src/ipc/sender-check.ts:95`): `scout:vault.store` and `delete`; `scout:github.saveToken`, `signIn`, `signOut`, `botCreate`, `botRemove` and `botSetWrites`; `scout:youtube.saveApiKey`, `clearApiKey`, `signIn` and `signOut`; `scout:discord.checkToken`, which stores the bot token; `scout:slack.save` and `disconnect`; `scout:usage.adminKeySave` and `adminKeyRemove`; `scout:chatgpt.signIn` and `signOut`; `scout:kubernetes.use` and `disconnect`, the contexts Scout may read; `scout:access.set` and `startPairing`, who may ask Scout in chat. Main registers the modules that own them through one guarded `ipcMain` (`credentialIpc`, `ui/packages/main/src/main.ts:435`, built by `guardChannels`, `ui/packages/main/src/ipc/sender-check.ts:70`). The refusal is `ipc/not-main-window`, "Credentials and access can be changed only in the Scout window." The renderer calls each of them from the main window only.
  - **Letting the quick-ask shortcut go answers only the main window** (added 2026-11-10): `scout:local.suspendShortcut`, which Settings calls while it records a new shortcut so the current one reaches the page instead of opening quick ask (`SHORTCUT_GUARDS`, `ui/packages/main/src/ipc/sender-check.ts:136`; the quick-ask module's handlers are registered through that guard, `ui/packages/main/src/main.ts:1092`). The refusal is `ipc/not-main-window`, "The quick-ask shortcut can be recorded only in the Scout window." The other `scout:local.*` channels are not guarded.
  - **The verdict on a pull request answers the windows that show its buttons:** `scout:github.giveVerdict` is the owner's per-action approval of an irreversible write ([[pages/decisions/0043-the-verdict-on-a-pull-request]]), so it is a decision. It answers the main and quick-ask windows, the two that render the review's findings and verdict buttons (`VERDICT_WINDOWS`, `ui/packages/main/src/ipc/github-verdict.ts:25`, guarded at `:147`). The refusal is `github/not-verdict-window`, "A verdict can be given only from the Scout window or quick ask."
  - **The floating button's card answers only the floating button** (2026-11-10): `scout:local.bubbleCard`, which shows or hides the card beside the button ([[pages/services/desktop-quick-ask]], Floating button), is guarded by `QUICK_ASK_GUARDS` (`ui/packages/main/src/ipc/sender-check.ts:160`), and main registers quick ask through that guarded `ipcMain` (`ui/packages/main/src/main.ts:1092`). The refusal is `ipc/not-floating-button`, "Only the floating button shows its card."; the card window itself is refused.
  - **Bringing the Scout window forward answers only the operator window** (added 2026-11-11): `scout:app.showMainWindow`, which the operator rail's Back to Scout calls ([[pages/services/desktop-operator]]), is guarded by `SHOW_MAIN_GUARDS` (`ui/packages/main/src/ipc/sender-check.ts:148`): the operator window's own page in its main frame, and nobody else. It takes no payload and only shows, restores and focuses the main window, so the worst a refused caller is denied is a window coming forward (`ui/packages/main/test/sender-check.test.ts:247`).
  - **The operator's decisions answer the operator and main windows:** the eleven `scout:operator.*` channels that decide on the owner's behalf or send an action to YouTube, `decideClaim`, `delegation`, `mediaRights`, `packageApproval`, `publication`, `proposalDecision`, `experiment`, `experimentAdoption`, `commentDecision`, `replyApproval` and `ideaDecision` (`OPERATOR_DECISION_CHANNELS`, `ui/packages/main/src/ipc/operator.ts:100`). Today only the operator window's pages call them. The refusal is `operator/not-operator-window`, "Operator decisions can be taken only from the operator or Scout window." The operator's editing and generation channels (scenes, narration, presenter, pictures, metadata, assembly) and `projectDeletion`, which changes only Scout's own operator state, are not guarded.

## Consequences

- **Better:** someone who can start the packaged app cannot run their own code with main's access through `ELECTRON_RUN_AS_NODE`, `NODE_OPTIONS` or `--inspect`. A script in a quick-ask, floating-button or operator renderer, or in any frame other than a window's own page, cannot grant, revoke, enable, disable, set up or change a feature's credentials, store or delete any credential, sign in or out, or change who may ask Scout; from the quick-ask window or the floating button it cannot take an operator decision; and from the floating button, the operator window (for a verdict) or a frame that is not a window's own page it cannot approve, reject or give a verdict.
- **Worse:** a packaged build can no longer be debugged with `--inspect`. Playwright attaches to main through `--inspect`, so the packaged smoke test drives a copy of the extracted app whose executable has that one fuse turned back on. It reads the fuses from the untouched shipped executable. Cookie encryption cannot be turned off again without corrupting the profile's cookie store.
- **Neutral but noteworthy:** the `file://` privileges stay granted, so a `file://` page can still `fetch` other `file://` URLs and give universal access to `file://` sub-frames. The content security policy and the blocked navigation and `window.open` limit what a renderer can load. The asar integrity check acts on macOS and Windows only. On Linux the smoke test proves that the fuse byte is set, not that a check runs. The macOS and Windows builds come from CI and are not run by any test here.

## Alternatives considered

- **`@electron/fuses` from an `afterPack` hook** - the same library behind a script of our own. electron-builder's `electronFuses` covers every fuse Scout sets and flips them in the right order, before signing.
- **Turn the file protocol privileges off now** - the packaged renderer does not load with it off (see the Decision). Serving it from a custom protocol is a change to every window's loading and to the content security policy, larger than this phase.
- **Leave the inspector fuse on for the smoke test** - the shipped binary would keep the inspector open to anyone who can start it, so the test would protect less than the release ships.
- **Guard by the route in the window's URL hash** - the hash is the renderer's own state and a script can change it. Identity of the `webContents` cannot be forged from a renderer.
- **Guard only the calls that turn writes on** (the state before this ADR) - a revoke, a disable or a credential delete from another window cannot add a write, but it can silently undo the owner's choice. These calls only ever come from the Features screen of the main window.

## Enforcement

- **Code rule / test** - `ui/e2e/smoke/packaged-app.spec.ts:193` reads the fuse wire of the shipped `scout-desktop` and asserts each fuse. `ui/packages/main/test/packaging-config.test.ts:136` asserts the `electronFuses` block, so CI catches a removed block without packaging. `ui/packages/main/test/ipc-features.test.ts:854`, `ui/packages/main/test/ipc-operator.test.ts:1728`, `ui/packages/main/test/ipc-github-verdict.test.ts:364`, `ui/packages/main/test/sender-check.test.ts:64` and, for the card channel, `ui/packages/main/test/sender-check.test.ts:198` with the quick-ask row "the card window itself is not the floating button" in `ui/packages/main/test/quick-ask.test.ts` run every guarded channel against the main window, the quick-ask window, the floating button, the operator window, an unknown renderer, a sub-frame of the main, the quick-ask and the operator window, the main window on another page, and an event without its frame. It asserts that the allowed senders reach the control plane, and that every other sender is refused with the typed error, reaches nothing and leaves exactly one warn line with the channel and the kind. `ui/packages/main/test/sender-check.test.ts:109` checks that main registers every module owning a credential channel through `credentialIpc` and that those modules own every channel in the table. `ui/packages/main/test/sender-check.test.ts:142` runs `scout:local.suspendShortcut` against the same senders (only the main window reaches it) and checks that main registers the quick-ask module through the shortcut guard.
- **Convention page** - [[pages/conventions/testing]] (the packaged smoke test).

## Related

- Services affected: [[pages/services/desktop-ui]], [[pages/services/desktop-quick-ask]], [[pages/services/desktop-operator]]
- Decisions: [[pages/decisions/0032-per-feature-write-permissions]], [[pages/decisions/0051-desktop-end-to-end-tests]] (the packaged smoke test)
- Initiative: [[pages/roadmap/security-hardening]]
