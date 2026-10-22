---
title: "AI Sources screen for inference backend discovery"
version: "1.1"
date: 2026-05-16
updated: 2026-10-22
changes: "Note pointing to ADR 0015 for the OpenAI and Gemini cards"
page_type: adr
status: accepted
description: "Decision to add an AI Sources screen for discovering inference backends, complementing the task-based Models screen."
source:
  - "obsidian-vault/scout/06-decisions/2026-05-16-ai-sources-screen.md"
reliability: medium
---

# AI Sources screen for inference backend discovery

## Context

The Models screen (see [[pages/decisions/0006-models-screen-task-based]]) is
organised around **tasks** — Transcription / Analysis / Translation —
and assumes a model has been downloaded somewhere. It does not surface
the lower layer: **where models live**, whether a local runtime is
installed at all, and where the user can plug in cloud credentials in
the future.

A separate top-level screen is needed to:

- Detect and surface the state of local inference runtimes (Ollama,
  LM Studio).
- Provide a single entry point to "Manage Downloaded Models".
- Stake out card slots for future cloud providers (ChatGPT Plus,
  OpenAI API, Google Gemini, OpenRouter, Custom API) without
  committing to credentials handling yet.

The reference UX comes from competitor desktop apps that use a similar
"AI Providers" pane. Scout's variant is **AI Sources** to avoid the
direct name collision and to imply "where do models come from" more
neutrally.

## Decisions

- New route **`/sources`** rendering `SourcesComponent` and a new
  side-nav item **"AI Sources"** placed **above Models** in
  `PostConnectShellComponent`. The remaining nav items (Models,
  Status, Logs, Settings) keep their existing order.
  Tradeoff: one more left-rail entry in exchange for a clear
  separation between "connections" (Sources) and "task selection"
  (Models).
- Layout is a single page with a **filter chip group** (All / Local /
  Cloud) in the top-right and **two sections**: "Local & On-Device"
  (3 cards) and "Cloud APIs" (5 cards). The filter toggles section
  visibility via a `signal<FilterKind>`. Tradeoff: no virtualised
  grid; eight cards is the natural ceiling here, more would push us
  to redesign.
- Cards are data-driven from `localCards` / `cloudCards` arrays in
  the component. Each `CardModel` declares `id`, `kind`, `title`,
  `description`, `iconPath`, `primaryAction`, optional secondary
  link, `primaryIsScan`, `disabled`. Tradeoff: card variations
  become an interface bump rather than a forked component.
- **Connection badge** semantics:
  - Ollama / LM Studio: badge is `Connected` if the IPC scan
    (`llmListOllamaModels` / `llmListLmStudioModels`) returns 200,
    regardless of how many models the user has pulled. A reachable
    daemon counts as installed. The first version coupled badge to
    `models.length > 0` and confused users who had a fresh install
    with zero models; reverted on 2026-05-16.
  - **Local Models** (the wrapper card representing locally-managed
    Whisper + LLM models): badge is `Connected` only when **a local
    runtime is reachable AND `LocalModelsStore.selected` has a value
    for at least one kind**. "Connected" therefore means the user has
    something usable, not just an installed daemon. Tradeoff: the
    runtime check and the selection check both have to be true,
    which can confuse users who installed Ollama but haven't picked a
    model yet. The "Manage" button gating below mitigates this.
  - Cloud cards: always `Not Connected` in this iteration.
- **"Manage Downloaded Models" button gating**: enabled iff Ollama
  or LM Studio is reachable (`runtimeReachable` computed). Clicking
  navigates to `/sources/local-models` (the screen built in
  [[pages/decisions/0008-local-models-real-downloads]]). When disabled, a
  tooltip reads "Install Ollama or LM Studio to manage downloaded
  models." Tradeoff: the button is enabled even when no model is
  downloaded — that's intentional, the screen behind it is *where*
  the user goes to download one.
- **Initial scans on mount**: `ngOnInit` fires `onScan('ollama')`
  and `onScan('lm-studio')` in parallel. Scan re-runs on the user's
  explicit click. Tradeoff: one extra HTTP request per supported
  runtime at app open; acceptable for two endpoints.
- **External links** ("Download Ollama", "Download LM Studio") are
  rendered as buttons (not `<a>`) and route through
  `bridge.appOpenExternal({ url })`. The renderer cannot navigate to
  arbitrary URLs directly under our CSP; main process is the only
  place allowed to call `shell.openExternal`, and the URL allow-list
  lives there. Tradeoff: one extra IPC hop per click.

## Cloud card slots (visual only)

> Resolved in part on 2026-10-22 by [[pages/decisions/0015-cloud-llm-providers-by-api-key]]: the OpenAI API and Google Gemini cards now take an API key, and ChatGPT Plus is marked unsupported. OpenRouter and Custom API remain as described here.

ChatGPT Plus / OpenAI API / Google Gemini / OpenRouter / Custom API
all render with the same visual treatment but `disabled: true` on
their primary actions. Tooltip on disabled actions says "Coming soon."
Each card carries the icon + description copy verbatim from the
reference UX; wiring real auth flows requires per-provider work
that is out of scope here.

## Deferrals

- LM Studio scan retrying. Today the scan happens once on mount and
  once on user click; no automatic re-scan on focus or after a delay.
- Cloud provider wiring (API key storage, OAuth for ChatGPT Plus,
  endpoint validation for Custom API). All five cards stay disabled
  until per-provider ADRs land.
- Persisting the filter selection across app restarts. Currently
  defaults to "All".

## Open questions

- Should LM Studio scan be allowed when Ollama is *also* reachable?
  Today both run in parallel and the badges are independent. If the
  user has both up but only wants to use one, this is purely
  visual noise but no functional issue.
