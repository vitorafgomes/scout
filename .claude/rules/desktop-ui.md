---
paths:
  - "ui/packages/main/src/**"
  - "ui/packages/preload/src/**"
  - "ui/packages/renderer/src/**"
  - "ui/packages/shared/src/**"
  - "ui/build/**"
---

# Desktop UI

Owns: first-run wizard that sets up the Discord bot from its token, the Slack setup, the GitHub sign-in and its token refresh, the YouTube sign-in and its token refresh, finding the owner's kubeconfig and keeping the contexts the owner allowed, the credential vault, worker lifecycle and log streaming, detection of installed chat clients and local inference backends, Ollama and Whisper model downloads, the tray, keep running and start at login, packaging.

Also owns the quick-ask window: the global shortcut that opens it, the microphone capture and WAV encoding in the renderer, the one-frame screenshot and the display-media rule that answers it, and the questions main writes to the worker's stdin ([[pages/services/adapter-local]] answers them).

Also owns the lifecycle of [[pages/services/host-control-plane]] and the gRPC client the Features and Review screens and the operator window use to reach it.

Does not own: any conversation, LLM call or plugin; those live in [[pages/services/host-console]] and behind it.

## Rules this project must keep

- **Secrets never live in source.** Here: `ui/packages/main/src/vault/safe-storage-vault.ts:164` encrypts with the OS keychain; the renderer never receives a secret back, only `has`, `isAvailable` and the two non-secret keys of `scout:vault.getPublic`.
- **Secrets never live in source.** Here: GitHub tokens and the device code stay in main: the status and sign-in payloads carry the user code, the login and flags only, and no log line carries a token.
- **Secrets never live in source.** Here: the kubeconfig stays where it is: main stores only its path and the allowed context names, and no detect, use, status or disconnect payload and no log line carries a token, key, certificate, password or exec env value.
- **Secrets never live in source.** Here: the Slack tokens travel renderer to main once, in `scout:slack.save`; the status answer carries the team and bot user names only, and no log line, answer or error message carries a token or the Socket Mode URL `apps.connections.open` returns.
- **Scout performs descriptive writes only.** Here: main's calls to Slack are `auth.test` and `apps.connections.open`, a read and a handshake whose URL is dropped unopened ([[pages/decisions/0023-slack-setup-from-a-manifest]]).
- **Scout performs descriptive writes only.** Here: main sends only GETs to `api.github.com` and POSTs only to GitHub's two sign-in endpoints.
- **Scout performs descriptive writes only.** Here: main POSTs only to Google's token and revoke endpoints and sends only GETs under `www.googleapis.com/youtube/v3/`; `accounts.google.com` is only opened in the browser.
- **Secrets never live in source.** Here: the Google OAuth client secret, the authorization code, the code verifier, the tokens and a pasted key stay in main: no log line, status or IPC answer carries one, and a rejected key's message never quotes it.
- **Secrets never live in source.** Here: feature credentials travel renderer to main to gRPC and are never logged: the Features handlers log the feature id and the error only, and the launcher never logs the session token or the worker token, nor sets either on its own environment.
- **Scout performs descriptive writes only.** Here: the login item and `~/.config/autostart/scout.desktop` are Scout's own startup entry, never reachable from the worker or chat ([[pages/decisions/0027-desktop-presence]]).
- **Scout performs descriptive writes only.** Here: a quick-ask reply is shown only in the owner's quick-ask window and Chat screen, which share one conversation held in main; main's stdin line and the microphone capture are reads the owner starts ([[pages/decisions/0028-local-quick-ask-assistant]]).
- **Secrets never live in source.** Here: quick-ask recordings stay in renderer memory until sent and are never written to disk; `SCOUT_LOCAL` lines, which carry the question and the reply, never enter the log ring or the log viewer.
- **Secrets never live in source.** Here: live listening keeps no audio: each chunk goes from the renderer to the worker's stdin and is dropped, no main log line carries a chunk or a transcript, and the transcripts reach the windows on `scout:local.transcript` only, never the conversation's turns (`ui/packages/main/test/quick-ask.test.ts`, the listening block).
- **ADR 0032 - Features declare what they write, and the owner grants each one.** Here: a screenshot is a declared read of the owner's screen: one frame, on the owner's click, after the system dialog where the system has one, with "Choose what Scout may see..." showing while the picker is open and every track stopped after the frame.
- **Ollama is the default LLM provider.** Here: the only network call for transcription is the model download from `huggingface.co/ggerganov/whisper.cpp`, and a download is kept only at the pinned size and SHA-256; its only writes are the model, its `.partial` and its `.sha256` sidecar under `userData/whisper-models`.
- **Scout performs descriptive writes only.** Here: the update check and the version list are each one GET of public release metadata, no credential, the version in the User-Agent; they write only Scout's own `update.*` vault keys.
- **ADR 0032 - Features declare what they write, and the owner grants each one.** Here: the desktop is the only approval surface: a write grant and an action decision reach the control plane only through the Features and Review screens over the token-authenticated socket, and the channels that carry them change Scout's own feature state and approval queue, nothing outside Scout.
- **ADR 0033 - Channel operator architecture: state in the control plane, pipeline in Scout.Operator, media through the connected AI Source, progress back to the originating channel.** Here: the operator window and its `scout:operator.*` channels change only Scout's own operator state and media folder, see [[pages/services/desktop-operator]].
- **Scout performs descriptive writes only.** Here: a meeting is written only to the worker's meetings database in Scout's own data folder, and only when the owner ticks Keep this meeting and clicks Done; an export is written only to the file the owner picks in the save dialog; a delete runs only on the owner's confirm.
- **Adapters, LLM providers and plugins are swapped by configuration.** Here: the UI reaches the console worker only through environment variables, stdout markers and quick-ask JSON lines on stdin, and the control plane only through gRPC; no .NET type crosses, and `@grpc/grpc-js` stays under `ui/packages/main/src/control-plane/` (ADR 0011: the renderer never sees it).

The full statement of each, with the code and tests that hold it: `context/pages/services/desktop-ui/how-the-rules-are-kept.md`.

## Pages that govern this project

Read one when your change touches what its title names.

- [[pages/invariants/secrets-never-in-source|Secrets never live in source]] - credentials go to the OS keychain through `safeStorage`, never to a file in the repo.
- [[pages/invariants/no-executive-writes|Scout performs descriptive writes only]] - the UI is the one place Scout mutates the host on the user's behalf (model pulls, worker process); the download and delete of local models are ruled a reviewed local write on the owner's click by the amendment of 2026-11-11 to [[pages/decisions/0032-per-feature-write-permissions|ADR 0032 - Features declare what they write, and the owner grants each one]], and starting the worker process is still not classified.
- [[pages/decisions/0004-web-stack-angular|ADR - Web stack is Angular]] and [[pages/decisions/0005-scout-desktop-m1-scope|Scout Desktop M1 scope and deferrals]] - the stack and the M1 deferrals.
- [[pages/decisions/0011-backend-frontend-grpc-uds|Backend-frontend transport - gRPC over Unix domain socket / named pipe]] - the transport; the Features screen uses it, and the desktop has no provider lifecycle channel (see Control plane below).
- [[pages/roadmap/control-plane-client|Control plane client]] - the open initiative that moves the remaining screens.
- [[pages/decisions/0019-github-app-device-flow|ADR 0019 - GitHub through a read-only GitHub App and the device flow]] - the GitHub sign-in that main owns.
- [[pages/decisions/0030-youtube-plugin-read-only-with-browser-sign-in|ADR 0030 - YouTube plugin: read-only over the official APIs, signed in from the desktop through the browser]] - the YouTube sign-in through the browser, or the pasted key, that main owns.
- [[pages/decisions/0032-per-feature-write-permissions|ADR 0032 - Features declare what they write, and the owner grants each one]] - features declare their writes, the owner grants them on the Features screen and approves each irreversible action on the Review screen.
- [[pages/decisions/0053-electron-fuses-and-ipc-sender-check|ADR 0053 - Electron fuses and the IPC sender check]] - the packaged app's fuses, and which window may call the grant and the decision channels.
- [[pages/decisions/0033-channel-operator-architecture|ADR 0033 - Channel operator architecture: state in the control plane, pipeline in Scout.Operator, media through the connected AI Source, progress back to the originating channel]] - the operator's state lives in the control plane; the operator window's pages read and edit it there and hold no pipeline logic, and the operator has its own desktop window, not a local web site ([[pages/services/desktop-operator|Desktop operator window]]).
- [[pages/decisions/0020-kubernetes-from-local-kubeconfig|ADR 0020 - Kubernetes from the kubeconfig on this computer, with consent per context]] - the Kubernetes contexts main reads from the owner's kubeconfig.
- [[pages/decisions/0021-allowed-users|ADR 0021 - Only allowed users can use Scout once an owner credential is connected]] - the allowed-users list main keeps and passes to the worker.
- [[pages/decisions/0022-discord-setup-from-the-bot-token|ADR 0022 - Discord setup from the bot token]] - how the wizard sets up the Discord bot, and why its one PATCH is Scout's own configuration.
- [[pages/decisions/0023-slack-setup-from-a-manifest|ADR 0023 - Slack is set up from a prefilled app manifest and two pasted tokens]] - the Slack app manifest and the two tokens main checks and keeps.
- [[pages/decisions/0027-desktop-presence|ADR 0027 - Scout stays resident on the desktop: tray, keep running, start at login, notifications]] - the tray, keep running, start at login and the three notifications, and why the autostart entry is Scout's own configuration.
- [[pages/decisions/0028-local-quick-ask-assistant|ADR 0028 - Quick ask: talk to Scout from the operating system, over the worker's stdio]] - the quick-ask window, its shortcut, the microphone and the stdio transport to the worker.
- [[pages/conventions/testing|Testing]] - `bun run test` per package, Vitest in the renderer.

## What to read for a change

Find the row your change matches and read that page. Read one or two rows, not the table.

| If the change touches | Read |
|---|---|
| Layout | `context/pages/services/desktop-ui/layout.md` |
| Since 2026-11-06 (pages/decisions/0041-angular-material-in-the-renderer) the renderer carries; Theme choice; Top bar; Icons; Notices | `context/pages/services/desktop-ui/theme-and-notices-since-2026-11-06-the-renderer.md` |
| Tests: ui/packages/renderer/src/styles.spec.ts (the base not dark,; Notification center | `context/pages/services/desktop-ui/theme-and-notices-tests-ui-packages-renderer-src.md` |
| ADR 0045 (pages/decisions/0045-theme-and-notification-center). NotificationStore (ui/packages/main/src/notifications/notification-store.ts:55) keeps | `context/pages/services/desktop-ui/theme-and-notices-adr-0045-notificationstore-kee.md` |
| Pause, Continue and Stop on a running job | `context/pages/services/desktop-ui/theme-and-notices-pause-continue-and-stop-on-a-r.md` |
| IPC channels; Vault keys; Cloud LLM cards; Claude API card; OpenRouter card; Organization spend (optional Admin key) | `context/pages/services/desktop-ui/public-contract-ipc-channels.md` |
| ChatGPT plan card; Models → Analysis | `context/pages/services/desktop-ui/public-contract-chatgpt-plan-card.md` |
| Models → Translation; Models → Speech | `context/pages/services/desktop-ui/public-contract-models-translation.md` |
| Models, as the prototype shows it; TypeSafe (Jev) card; Usage on the cards; Usage store and OpenRouter read (main) | `context/pages/services/desktop-ui/public-contract-models-as-the-prototype-shows-it.md` |
| Loading, buttons and Disconnect | `context/pages/services/desktop-ui/public-contract-loading-buttons-and-disconnect.md` |
| Ollama models on the Ollama card; GitHub card; Kubernetes card; YouTube card | `context/pages/services/desktop-ui/public-contract-ollama-models-on-the-ollama-card.md` |
| Worker handshake; Routes | `context/pages/services/desktop-ui/public-contract-worker-handshake.md` |
| How it works | `context/pages/services/desktop-ui/how-it-works.md` |
| At app start ui/packages/main/src/main.ts:635 calls connectControlPlane; The launcher mints two tokens: the; The Features IPC handlers (ui/packages/main/src/ipc/features.ts:67) call; Header; Control plane not... | `context/pages/services/desktop-ui/control-plane-at-app-start-ui-packages-main-src.md` |
| Error codes across IPC; Main has no HTTP client for; Worker restarts; The worker reads its provider, models,; Three callers use it: the renderer's; A card that asked shows main's; Whisper models; The pinned model... | `context/pages/services/desktop-ui/control-plane-error-codes-across-ipc.md` |
| The worker registers its Whisper transcriber; Local Models screen; There is no Local Models screen; On the Ollama view (the panel | `context/pages/services/desktop-ui/control-plane-the-worker-registers-its-whisper-t.md` |
| pages/decisions/0032-per-feature-write-permissions on the desktop side. The; The panel's permissions; Write-verb allow-list; The grant; A toggle feature; A refusal | `context/pages/services/desktop-ui/write-permissions-pages-decisions-0032-per-featu.md` |
| Credentials follow the grant; The worker | `context/pages/services/desktop-ui/write-permissions-credentials-follow-the-grant.md` |
| The Review screen (route review, ui/packages/renderer/src/app/shell/post-connect-shell.component.ts:36;; Waiting for you; History; The wording; Actions; Outcomes; Approved by; Times; The notice after a decision;... | `context/pages/services/desktop-ui/review-the-review-screen-route-review-ui-package.md` |
| The screen loads both lists on; The look; Text; Loading; The count on Waiting for you; The reason field; The alert; Chips and code; History's table; Tests: the Approvals shows each state | `context/pages/services/desktop-ui/review-the-screen-loads-both-lists-on.md` |
| Meetings | `context/pages/services/desktop-ui/meetings.md` |
| Operator window | `context/pages/services/desktop-ui/operator-window.md` |
| GitHub connection | `context/pages/services/desktop-ui/github-connection.md` |
| pages/decisions/0047-the-docker-card parts 1 and 2: the; Main's docker module; The snapshots folder; The work folder; Channels; The switch; The credential dialog of a control-plane feature; The voice model dialog;... | `context/pages/services/desktop-ui/docker-card-pages-decisions-0047-the-docker-card.md` |
| The panel; Sections the owner hides; The GitHub panel's hint; Tests | `context/pages/services/desktop-ui/docker-card-the-panel.md` |
| Kubernetes connection | `context/pages/services/desktop-ui/kubernetes-connection.md` |
| The YouTube side panel holds sc-youtube-connect; GoogleAuth (ui/packages/main/src/oauth/google-auth.ts:109) owns both credentials, one; Sign in with Google; OAuth client; Token file; Refresh; Disconnect; Allow... | `context/pages/services/desktop-ui/youtube-connection-the-youtube-side-panel-holds.md` |
| Add an API key; Pending; Connected; Open YouTube operator; Failed; Tests; Like the GitHub card, it sits; Disconnect asks first | `context/pages/services/desktop-ui/youtube-connection-add-an-api-key.md` |
| Voice model on the Desktop card | `context/pages/services/desktop-ui/voice-model-on-the-desktop-card.md` |
| The wizard needs only the bot; checkBotToken reads GET /applications/@me (:66).; enableMessageContent adds GATEWAY_MESSAGE_CONTENT_LIMITED to the existing; inviteUrl builds a scope=bot invite with; listGuilds reads... | `context/pages/services/desktop-ui/discord-setup-the-wizard-needs-only-the-bot.md` |
| The setup frame and its steps follow the artboard; Create bot; Bot token; Add to server; Launch; Ready; In the artboard and not built; Tests | `context/pages/services/desktop-ui/discord-setup-the-setup-frame-and-its-steps-foll.md` |
| Allowed users | `context/pages/services/desktop-ui/allowed-users.md` |
| Slack setup | `context/pages/services/desktop-ui/slack-setup.md` |
| Settings follows its artboard, context/prototypes/desktop/Settings.dc.html (pages/roadmap/prototype-migration,; Chat connections; Scout worker; Who can use Scout | `context/pages/services/desktop-ui/settings-settings-follows-its-artboard-context-p.md` |
| Audio; Appearance; Logs screen; The Logs screen (route logs, in | `context/pages/services/desktop-ui/settings-audio.md` |
| Quick ask | `context/pages/services/desktop-ui/quick-ask.md` |
| Desktop presence | `context/pages/services/desktop-ui/desktop-presence.md` |
| the evidence for a rule of this project: the code and the tests that hold it | `context/pages/services/desktop-ui/how-the-rules-are-kept.md` |
| Hot spots | `context/pages/services/desktop-ui/hot-spots.md` |
| Tests | `context/pages/services/desktop-ui/tests.md` |

For where a type lives or who calls it, ask the code graph (`graphify explain "<Type>"`, `graphify affected "<Type>"`).
