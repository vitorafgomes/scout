---
title: "Desktop UI"
version: "1.117"
date: 2026-10-22
updated: 2026-11-12
changes: "v1.117 (2026-11-12): a vault check that fails keeps the localStorage mirror of the selection (a cloud provider, the ChatGPT plan or Ollama) instead of wiping it; a failed vault availability check shows the unavailable banner; Clear of the Logs screen does not throw when storage is unavailable."
page_type: service
status: active
description: "Electron desktop app (Bun workspace, Angular renderer): onboards a Discord bot or a Slack app, keeps credentials in the OS keychain, spawns and supervises the Scout.Host.Console worker, and manages local Ollama and Whisper models from Node."
source:
  - ui/packages/main/src
  - ui/packages/preload/src
  - ui/packages/renderer/src
  - ui/packages/shared/src
  - ui/build
reliability: high
---

# Desktop UI

> Onboards a chat bot, stores its secrets, launches and supervises the .NET worker, and manages local models; it never calls the LLM itself.

Dated changes: [[pages/history/desktop-ui]].

## Responsibility

Owns: first-run wizard that sets up the Discord bot from its token, the Slack setup, the GitHub sign-in and its token refresh, the YouTube sign-in and its token refresh, finding the owner's kubeconfig and keeping the contexts the owner allowed, the credential vault, worker lifecycle and log streaming, detection of installed chat clients and local inference backends, Ollama and Whisper model downloads, the tray, keep running and start at login, packaging.

Also owns the quick-ask window: the global shortcut that opens it, the microphone capture and WAV encoding in the renderer, the one-frame screenshot and the display-media rule that answers it, and the questions main writes to the worker's stdin ([[pages/services/adapter-local]] answers them).

Also owns the lifecycle of [[pages/services/host-control-plane]] and the gRPC client the Features and Review screens and the operator window use to reach it.

Does not own: any conversation, LLM call or plugin; those live in [[pages/services/host-console]] and behind it.

## Rules

The rules this project must keep, and what to read before changing it, are in `.claude/rules/desktop-ui.md`. Claude Code loads that file when a task touches the project.

## Topics

Read the page your change touches, not all of them.

| Page | Read when a change touches | Characters |
|---|---|---|
| [[pages/services/desktop-ui/layout\|Layout]] | Layout | 2602 |
| [[pages/services/desktop-ui/theme-and-notices-since-2026-11-06-the-renderer\|Theme and notices: Since 2026-11-06 (pages/decisions/0041-angular-material-in-the-renderer) the renderer carries]] | Since 2026-11-06 (pages/decisions/0041-angular-material-in-the-renderer) the renderer carries; Theme choice; Top bar; Icons; Notices | 11566 |
| [[pages/services/desktop-ui/theme-and-notices-tests-ui-packages-renderer-src\|Theme and notices: Tests: ui/packages/renderer/src/styles.spec.ts (the base not dark,]] | Tests: ui/packages/renderer/src/styles.spec.ts (the base not dark,; Notification center | 3288 |
| [[pages/services/desktop-ui/theme-and-notices-adr-0045-notificationstore-kee\|Theme and notices: ADR 0045 (pages/decisions/0045-theme-and-notification-center). NotificationStore (ui/packages/main/src/notifications/notification-store.ts:55) keeps]] | ADR 0045 (pages/decisions/0045-theme-and-notification-center). NotificationStore (ui/packages/main/src/notifications/notification-store.ts:55) keeps | 12432 |
| [[pages/services/desktop-ui/theme-and-notices-pause-continue-and-stop-on-a-r\|Theme and notices: Pause, Continue and Stop on a running job]] | Pause, Continue and Stop on a running job | 6142 |
| [[pages/services/desktop-ui/public-contract-ipc-channels\|Public contract: IPC channels]] | IPC channels; Vault keys; Cloud LLM cards; Claude API card; OpenRouter card; Organization spend (optional Admin key) | 11589 |
| [[pages/services/desktop-ui/public-contract-chatgpt-plan-card\|Public contract: ChatGPT plan card]] | ChatGPT plan card; Models → Analysis | 11570 |
| [[pages/services/desktop-ui/public-contract-models-translation\|Public contract: Models → Translation]] | Models → Translation; Models → Speech | 8035 |
| [[pages/services/desktop-ui/public-contract-models-as-the-prototype-shows-it\|Public contract: Models, as the prototype shows it]] | Models, as the prototype shows it; TypeSafe (Jev) card; Usage on the cards; Usage store and OpenRouter read (main) | 11174 |
| [[pages/services/desktop-ui/public-contract-loading-buttons-and-disconnect\|Public contract: Loading, buttons and Disconnect]] | Loading, buttons and Disconnect | 2122 |
| [[pages/services/desktop-ui/public-contract-ollama-models-on-the-ollama-card\|Public contract: Ollama models on the Ollama card]] | Ollama models on the Ollama card; GitHub card; Kubernetes card; YouTube card | 12148 |
| [[pages/services/desktop-ui/public-contract-worker-handshake\|Public contract: Worker handshake]] | Worker handshake; Routes | 2103 |
| [[pages/services/desktop-ui/how-it-works\|How it works]] | How it works | 10212 |
| [[pages/services/desktop-ui/control-plane-at-app-start-ui-packages-main-src\|Control plane: At app start ui/packages/main/src/main.ts:635 calls connectControlPlane]] | At app start ui/packages/main/src/main.ts:635 calls connectControlPlane; The launcher mints two tokens: the; The Features IPC handlers (ui/packages/main/src/ipc/features.ts:67) call; Header; Control plane not... | 12031 |
| [[pages/services/desktop-ui/control-plane-error-codes-across-ipc\|Control plane: Error codes across IPC]] | Error codes across IPC; Main has no HTTP client for; Worker restarts; The worker reads its provider, models,; Three callers use it: the renderer's; A card that asked shows main's; Whisper models; The pinned model... | 10823 |
| [[pages/services/desktop-ui/control-plane-the-worker-registers-its-whisper-t\|Control plane: The worker registers its Whisper transcriber]] | The worker registers its Whisper transcriber; Local Models screen; There is no Local Models screen; On the Ollama view (the panel | 10895 |
| [[pages/services/desktop-ui/write-permissions-pages-decisions-0032-per-featu\|Write permissions: pages/decisions/0032-per-feature-write-permissions on the desktop side. The]] | pages/decisions/0032-per-feature-write-permissions on the desktop side. The; The panel's permissions; Write-verb allow-list; The grant; A toggle feature; A refusal | 12026 |
| [[pages/services/desktop-ui/write-permissions-credentials-follow-the-grant\|Write permissions: Credentials follow the grant]] | Credentials follow the grant; The worker | 6885 |
| [[pages/services/desktop-ui/review-the-review-screen-route-review-ui-package\|Review: The Review screen (route review, ui/packages/renderer/src/app/shell/post-connect-shell.component.ts:36;]] | The Review screen (route review, ui/packages/renderer/src/app/shell/post-connect-shell.component.ts:36;; Waiting for you; History; The wording; Actions; Outcomes; Approved by; Times; The notice after a decision;... | 11662 |
| [[pages/services/desktop-ui/review-the-screen-loads-both-lists-on\|Review: The screen loads both lists on]] | The screen loads both lists on; The look; Text; Loading; The count on Waiting for you; The reason field; The alert; Chips and code; History's table; Tests: the Approvals shows each state | 5562 |
| [[pages/services/desktop-ui/meetings\|Meetings]] | Meetings | 8631 |
| [[pages/services/desktop-ui/operator-window\|Operator window]] | Operator window | 591 |
| [[pages/services/desktop-ui/github-connection\|GitHub connection]] | GitHub connection | 511 |
| [[pages/services/desktop-ui/docker-card-pages-decisions-0047-the-docker-card\|Docker card: pages/decisions/0047-the-docker-card parts 1 and 2: the]] | pages/decisions/0047-the-docker-card parts 1 and 2: the; Main's docker module; The snapshots folder; The work folder; Channels; The switch; The credential dialog of a control-plane feature; The voice model dialog;... | 10886 |
| [[pages/services/desktop-ui/docker-card-the-panel\|Docker card: The panel]] | The panel; Sections the owner hides; The GitHub panel's hint; Tests | 10260 |
| [[pages/services/desktop-ui/kubernetes-connection\|Kubernetes connection]] | Kubernetes connection | 6015 |
| [[pages/services/desktop-ui/youtube-connection-the-youtube-side-panel-holds\|YouTube connection: The YouTube side panel holds sc-youtube-connect]] | The YouTube side panel holds sc-youtube-connect; GoogleAuth (ui/packages/main/src/oauth/google-auth.ts:109) owns both credentials, one; Sign in with Google; OAuth client; Token file; Refresh; Disconnect; Allow... | 9911 |
| [[pages/services/desktop-ui/youtube-connection-add-an-api-key\|YouTube connection: Add an API key]] | Add an API key; Pending; Connected; Open YouTube operator; Failed; Tests; Like the GitHub card, it sits; Disconnect asks first | 7713 |
| [[pages/services/desktop-ui/voice-model-on-the-desktop-card\|Voice model on the Desktop card]] | Voice model on the Desktop card | 4937 |
| [[pages/services/desktop-ui/discord-setup-the-wizard-needs-only-the-bot\|Discord setup: The wizard needs only the bot]] | The wizard needs only the bot; checkBotToken reads GET /applications/@me (:66).; enableMessageContent adds GATEWAY_MESSAGE_CONTENT_LIMITED to the existing; inviteUrl builds a scope=bot invite with; listGuilds reads... | 11417 |
| [[pages/services/desktop-ui/discord-setup-the-setup-frame-and-its-steps-foll\|Discord setup: The setup frame and its steps follow the artboard]] | The setup frame and its steps follow the artboard; Create bot; Bot token; Add to server; Launch; Ready; In the artboard and not built; Tests | 6457 |
| [[pages/services/desktop-ui/allowed-users\|Allowed users]] | Allowed users | 4828 |
| [[pages/services/desktop-ui/slack-setup\|Slack setup]] | Slack setup | 3300 |
| [[pages/services/desktop-ui/settings-settings-follows-its-artboard-context-p\|Settings: Settings follows its artboard, context/prototypes/desktop/Settings.dc.html (pages/roadmap/prototype-migration,]] | Settings follows its artboard, context/prototypes/desktop/Settings.dc.html (pages/roadmap/prototype-migration,; Chat connections; Scout worker; Who can use Scout | 10577 |
| [[pages/services/desktop-ui/settings-audio\|Settings: Audio]] | Audio; Appearance; Logs screen; The Logs screen (route logs, in | 10675 |
| [[pages/services/desktop-ui/quick-ask\|Quick ask]] | Quick ask | 1402 |
| [[pages/services/desktop-ui/desktop-presence\|Desktop presence]] | Desktop presence | 4075 |
| [[pages/services/desktop-ui/how-the-rules-are-kept\|How the rules are kept]] | the evidence for a rule of this project: the code and the tests that hold it | 9322 |
| [[pages/services/desktop-ui/hot-spots\|Hot spots]] | Hot spots | 2961 |
| [[pages/services/desktop-ui/tests\|Tests]] | Tests | 8915 |
