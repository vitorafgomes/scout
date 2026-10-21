---
title: "Models screen restructured by task (Transcription / Analysis / Translation)"
version: "1.0"
date: 2026-05-15
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: adr
status: accepted
description: "Decision to restructure the Models screen around tasks (Transcription, Analysis, Translation) instead of a single Ollama model picker."
source:
  - "obsidian-vault/scout/06-decisions/2026-05-15-models-screen-task-based.md"
reliability: medium
---

# Models screen restructured by task (Transcription / Analysis / Translation)

## Context

The current `/llm` screen in Scout Desktop is single-purpose: it lets the user
pick one Ollama model that the worker uses to compose chat answers. The
screen is titled "LLM", its top-level radio group is "Provider"
(Ollama / OpenAI / Azure), and the rest of the page is a single
endpoint + model picker.

This shape is too narrow for where Scout is heading. Scout needs to handle
multiple model-driven tasks beyond chat composition — at minimum
speech-to-text (Whisper) for voice inputs, and likely translation as a
separate cross-cutting concern. Forcing all of that into a single "LLM"
screen with one provider/model pair is misleading: the chat-completion
model that drives Analysis (`llama3.x`, `qwen2.5`, …) is a different
class of artifact from the STT model that drives Transcription (Whisper
Large v3, Whisper Large v3 Turbo).

The proposed redesign reframes the screen around **tasks** rather than
**providers**. Each task gets its own panel with the providers and
models appropriate for it. The provider/model pair stops being a global
selection and becomes per-task. The screen is renamed accordingly.

## Decisions

- The side-nav label and route stay at `/llm` for now (no router or
  bookmark breakage), but the **page UI is reframed as "Models"**. The
  title shown at the top of each panel is the active task name
  (`Transcription`, `Analysis`, `Translation`), not the word "Models".
  Tradeoff: minor inconsistency between route name and visible label
  in exchange for keeping all in-progress vault keys (`llm.provider`,
  `llm.model`, `llm.endpoint`) and routes valid until a separate
  rename ADR lands.
- The top of the page is a **segmented tab control** with three tabs:
  Transcription, Analysis, Translation. Tabs replace the existing
  provider radio group. Tradeoff: one more component to maintain in
  exchange for an obvious mental model — pick the task first, then
  the model.
- Translation is rendered but marked as **coming soon** (small badge
  on the tab, "Coming soon" message in the panel, tab is disabled).
  Tradeoff: visible-but-inert UI in exchange for surfacing roadmap
  intent now without committing to an adapter.
- Transcription panel offers exactly one provider group, **Local
  Whisper (On Device)**, with two selectable models: Whisper Large v3
  and Whisper Large v3 Turbo. No remote Whisper option in this
  iteration. Tradeoff: cloud Whisper users wait in exchange for
  preserving the local-first default that defines Scout.
- Analysis panel re-uses the **current LLM flow verbatim** — Ollama
  provider, endpoint input, model dropdown populated from
  `/api/tags`, Refresh + Save buttons. No behaviour change beyond
  being nested under a tab. Tradeoff: visual asymmetry between
  Transcription (card-based) and Analysis (form-based) in exchange
  for not blocking this UI change on a wider redesign.
- Persistence: a **second namespace** is added to the vault for
  transcription settings (`stt.provider`, `stt.model`). Existing
  `llm.*` keys keep their current meaning and continue to drive
  Analysis. `LlmConfigStore` gains a sibling
  `TranscriptionConfigStore` rather than being widened. Tradeoff:
  two stores instead of one polymorphic store in exchange for type
  safety and isolation — touching transcription cannot break chat.
- No backend changes in this ADR's scope. Saving a transcription
  model writes to the vault, but the worker is **not** yet wired to
  consume those keys. The worker continues to ignore STT
  configuration until a follow-up ADR lands a Whisper adapter
  (`Scout.Stt.WhisperLocal` or equivalent). Tradeoff: a save action
  that has no runtime effect in exchange for shipping the screen
  redesign independently of the STT adapter.
- The Transcription panel surfaces a **warning bar** when the worker
  is running ("Stop transcription to change the transcription
  model"). Until the worker actually owns STT, this bar is gated on
  the existing worker supervisor state (`running`). Tradeoff: copy
  that slightly overpromises in exchange for matching the agreed
  visual design from day one.
- Read-only invariant: transcription is **read-only by definition**
  — Whisper consumes audio and emits text, never writes externally.
  No change to Scout's invariant. Tradeoff: none.
- Tests: keep current Vitest patterns. New specs for the tab
  component, the Transcription panel, and the
  `TranscriptionConfigStore` round-trip. Existing
  `llm.component.spec.ts` keeps testing Analysis behaviour (renamed
  asserting the right title) but otherwise unchanged. Tradeoff: more
  test files in exchange for tight per-feature regression coverage.

## Amendments (2026-05-16)

The original draft kept Analysis backed by the legacy `LlmComponent`
and the side-nav label at "LLM". Both moved during implementation:

- Side-nav label was renamed to **"Models"** the same day, keeping
  the `/llm` route stable for bookmark compatibility. The page H1
  reflects the active tab name as planned.
- `LlmComponent` is **no longer mounted** anywhere — the Analysis
  panel was rewritten to consume `LocalModelsStore` like Transcription.
  Selecting a model in Analysis now persists via that store, not via
  `LlmConfigStore` (which is orphan code pending removal).
- The "Manage Downloaded Models" path landed as `/sources/local-models`
  with a dedicated `LocalModelsComponent`. See
  [[pages/decisions/0007-ai-sources-screen]] and
  [[pages/decisions/0008-local-models-real-downloads]].
- The `stt.provider` / `stt.model` vault keys reserved by this ADR were
  never wired to the worker. Selection state for Transcription now lives
  inside `LocalModelsStore.selected.transcription` (localStorage key
  `scout:local-models`). The `stt.*` keys are still in `VaultKey` but
  unused — candidate for cleanup once `TranscriptionConfigStore` is
  removed.

## Deferrals

- Whisper inference on the .NET worker (Whisper.net binding around
  whisper.cpp). The model file is now downloaded in real-time
  (see [[pages/decisions/0008-local-models-real-downloads]]), but the worker
  does not yet read it for transcription. Separate ADR + plan.
- Translation task: provider choice, model list, language matrix,
  default behaviour. Tab stays disabled until that ADR.
- Cloud Whisper providers (OpenAI Whisper API, Azure OpenAI Whisper).
- Audio source pipeline. Whether Scout listens to Discord voice
  channels, ingests uploaded files, or both is a product decision
  that comes before the worker inference ADR.

## Open questions

- Does the warning bar belong on every task panel, or only when the
  worker is actively using the model being edited? For now, gate
  on `worker.state === 'running'` on Transcription only.
- Should `LlmConfigStore` and `TranscriptionConfigStore` be removed
  entirely? Both are orphan code after the Analysis rewrite. Deferred
  to a cleanup PR.
