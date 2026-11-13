---
title: "Versions architecture"
version: "1.0"
date: 2026-11-13
changes: "Version notes moved out of page headers."
page_type: knowledge
status: active
description: "Version notes that were kept in page headers."
source: ["context/pages/"]
---

## Version notes: `pages/architecture/code-review-flow.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- v1.1 (2026-11-10): the skill carries every step the owner asked for; the markers removed and each item moved into what the review does; marking a conversation resolved stays the one thing Scout cannot do.
- v1.0: initial page: the flow of a pull request review from the request to the follow-up, with what is checked at each step and what is not in the skill yet (2026-11-10).

## Version notes: `pages/architecture/desktop-models-system.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- v1.11 (2026-11-12) a model whose known capabilities lack tools is marked No tools, and the model in use says it; choosing stays allowed. Earlier: v1.10 (2026-11-12) Pull by name: any model of the Ollama library pulled by its name through the same download path, the name checked in the dialog and in main, the list read again after every pull, the pull failures in one vocabulary. Earlier: v1.9 (2026-11-12) every model the local Ollama lists is a row and can be chosen: a model outside the catalog is kept under its tag with the details and capabilities main reads, an embeddings-only model is never a chat choice, and the model in use cannot be removed. Earlier: v1.8 (2026-11-12) scout:llm.unloadOllamaModels joins the local writes: Scout's own loaded models unloaded when the provider in use moves away from Ollama. Earlier: v1.7 (2026-11-11) refreshOllama replaces refreshSkillModel: one list read for the LLM Server panel and the Skill matching row, ollamaAnswers. Earlier: v1.6 (2026-11-11) the skill-matching model nomic-embed-text: pulled with an Ollama chat model while Ollama lacks it, shown in its own row, never a chat choice, its name passed to the worker. Earlier: v1.5 (2026-11-11) AI Sources has no Local Models card and no Manage Downloaded Models button: Ollama models are pulled from the Ollama card's Manage local models, the Whisper models live in Models, Transcription. Earlier: v1.4 (2026-11-11) a pointer to the Ollama and local models tracker. Earlier: v1.3 (2026-11-08) the write channels are reviewed against the write contract of ADR 0032. Earlier: v1.2 (2026-11-08) the Ollama pull flow without the identity bootstrapping, ending only on success, with Cancel and the stall window; downloads run in LocalModelsStore; TranscriptionConfigStore removed.
- v1.1 (2026-11-07) the note on the unmounted LlmComponent removed with the component. Earlier: side-nav block brought to the current order: Chat first, Features, no Status, Logs optional

## Version notes: `pages/architecture/desktop-ui-design-system.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- v1.9 (2026-11-12) The --sc-unread token for the bell's unread row; citations re-pointed by content.
- v1.8 (2026-11-10) The finding card and test chip tokens as the QuickAsk artboard draws them.
- v1.7 (2026-11-09) The status chip, the Material button and badge overrides, the danger token; Inter ships inside the app.
- v1.6 (2026-11-08) The typeface: one stack on the page that every component without a font of its own inherits.
- v1.5 (2026-11-07) The operator pages and the setup wizard follow the theme; the media tile token.
- v1.4 (2026-11-07) Models, Settings and the Whisper models follow the theme; the Ollama panel is no longer pinned.
- v1.3 (2026-11-07) The quick-ask window no longer pinned dark.
- v1.2 (2026-11-07) Theme tokens: which screens follow the theme and which stay on the dark canvas.
- v1.1 (2026-11-07) Theme tokens: the light and dark values the shipped stylesheet defines (ADR 0045).
- v1.0 migrated from the owner's notes on 2026-10-21

## Version notes: `pages/architecture/project-map.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- Host.Console references Scout.Sandbox and adapts it to the GitHub plugin's ISandboxRunner (2026-11-07). Earlier: Scout.Audit, the whole-repository audit job over Llm, Conversation and Knowledge, referenced by Host.Console (2026-11-07). Earlier: Scout.Sandbox, the container test runner, a leaf no project references yet (2026-11-07). Earlier: Plugin.GitHub references Conversation for the writes of ADR 0038 (2026-11-06). Earlier: Scout.Meetings, the kept meetings over SQLite, referenced by Host.Console. Earlier: Host.Console.Tests references Host.ControlPlane
