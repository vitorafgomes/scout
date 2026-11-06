---
title: "ADR 0035 - A screenshot attached to a question: one JPEG on the ask line, an image part for a provider that can see, a plain sentence otherwise"
version: "1.1"
date: 2026-11-04
changes: "Related: the text of a screenshot can also be read locally (ADR 0036)"
page_type: adr
status: accepted
description: "The quick-ask window may attach one screenshot the owner captured to the next text or voice question; the worker accepts it only as a JPEG of at most 3 MB on the ask line, sends it to the connected AI source as an image part of the user message when that source can read images, and otherwise answers with one sentence saying it cannot, without calling the model; the picture is never stored and never logged."
supersedes: ""
superseded_by: ""
source:
  - chat
  - src/Scout.Adapter.Local/LocalChatAdapter.cs
  - src/Scout.Conversation/Abstractions/ImageAttachment.cs
  - src/Scout.Conversation/Routing/LlmStreamingRouter.cs
  - src/Scout.Conversation/Streaming/StreamPump.cs
  - src/Scout.Llm/ChatGateway.cs
  - src/Scout.Llm/Abstractions/MediaCapabilities.cs
  - src/Scout.ControlPlane/Features/FeatureCatalog.cs
  - ui/packages/renderer/src/app/quick-ask/screen-grab.ts
  - ui/packages/main/src/quick-ask/quick-ask.ts
reliability: high
---

# ADR 0035 - A screenshot attached to a question: one JPEG on the ask line, an image part for a provider that can see, a plain sentence otherwise

> The owner may attach one screenshot to the next quick-ask question; Scout sends it to the connected AI source as part of that question when the source can read images, and says plainly that it cannot otherwise, and keeps the picture nowhere.

## Status

- [ ] Proposed
- [x] Accepted
- [ ] Deprecated / superseded

Updated: 2026-11-04.

## Context

The owner asked that Scout "capture what is on the screen" and answer about it, phase 2 of [[pages/roadmap/live-assistant]]. The quick-ask window already sends each question to the worker as one JSON line on its stdin ([[pages/decisions/0028-local-quick-ask-assistant]]), next to the live transcription requests of [[pages/decisions/0034-live-transcription-on-the-local-adapter]], and the worker reached the model with text only. Not every AI source the owner can connect reads images: Ollama runs whatever model the owner pulled, and Scout does not know whether it has vision. Two questions were open on the roadmap: what happens with a source that cannot read images, and how large an image the stdio transport carries. The model and the chat may only ask ([[pages/invariants/no-executive-writes]]), and no vendor type may cross into the conversation core ([[pages/invariants/pluggable-boundaries]]).

## Decision

- **Desktop capture.** The owner clicks Attach screenshot; the renderer asks for `getDisplayMedia` with video only, takes one frame scaled to at most 1920x1080 and encodes it as JPEG at quality 0.8 (`ui/packages/renderer/src/app/quick-ask/screen-grab.ts:17`). Main answers that request only for the app's own page and only for video, through the display-media request handler and `desktopCapturer.getSources` with no thumbnails, which on Wayland opens the system portal's picker (`ui/packages/main/src/quick-ask/quick-ask.ts:224`); every capture is one click and one choice by the owner, and nothing captures on a timer.
- **Wire.** Either ask kind, `{"type":"ask","id","text"}` or the voice ask with `audio`, may carry `"image":{"contentType":"image/jpeg","base64"}`. Main accepts up to 4,000,000 base64 characters (`ui/packages/main/src/quick-ask/quick-ask.ts:105`). The worker accepts only `image/jpeg`, compared without case, of 1 byte to 3 MB (3,145,728 bytes) decoded, and refuses anything else with `fail` "The screenshot must be a JPEG of at most 3 MB." before any handler runs (`src/Scout.Adapter.Local/LocalChatAdapter.cs:298`, `:364`). An accepted image becomes `ChatMessage.Image`, an `ImageAttachment(ContentType, Bytes)` (`src/Scout.Conversation/Abstractions/ImageAttachment.cs:9`); the voice rewrite to the transcript keeps it (`src/Scout.Conversation/ConversationConductor.cs:147`).
- **To the model.** The router copies it to `ChatRequest.Image`, a `ChatImage` of bytes and a MIME type (`src/Scout.Conversation/Routing/LlmStreamingRouter.cs:84`). The gateway sends the current user message as a `TextContent` and a Semantic Kernel `ImageContent`, on the direct path and on the Project Expert agent path (`src/Scout.Llm/ChatGateway.cs:252`); `ImageContent` exists only there. Replayed history and conversation memory carry the text only.
- **Vision rule: fail closed.** `MediaCapabilities.Vision` says whether the provider's chat service accepts image parts (`src/Scout.Llm/Abstractions/MediaCapabilities.cs:29`). With an image and no vision, the gateway throws `LlmVisionUnavailableException` before any call leaves the process (`src/Scout.Llm/ChatGateway.cs:77`), and the reply completes with "The connected AI source cannot look at pictures; ask it in words or connect one that can." (`src/Scout.Conversation/Streaming/StreamPump.cs:146`). The question is not answered as if no picture had been sent.
- **Per-provider Vision.**

| Provider | Vision | Why |
|---|---|---|
| OpenAI | true, on any endpoint | its chat model family reads images; a custom endpoint keeps it, unlike speech and pictures |
| AzureOpenAI | true | the chat deployment is mandatory, and its OpenAI models read images |
| OpenRouter | true | it routes to vision models through the OpenAI chat format |
| ChatGptPlan | true | the Responses API takes an `input_image` part, checked over the real client |
| Ollama | false | the pulled model is unknown; a later change may read the model's capabilities |
| Gemini, Mistral, HuggingFace, AzureAIInference, None | false | not checked for image parts in this change |

- **Never stored or logged.** The picture lives only in the request's objects: no file, no cache, no memory turn. Log lines say `image=true` or `image=false` and, on the adapter, the byte count; `ChatMessage.ToString` prints `Image = <n> bytes`. The only place the bytes go is the existing chat call to the provider the owner connected, a descriptive call with one more part.
- **Declared read.** The catalog gains the `desktop` feature, under `WriteClass.None` with no credentials, with the read rows `Screen` and `Microphone` (`src/Scout.ControlPlane/Features/FeatureCatalog.cs:185`), so the Features screen lists what the desktop captures ([[pages/decisions/0032-per-feature-write-permissions]]). It gates nothing: the capture already needs the owner's click and the system picker.

## Consequences

- **Better:** the owner asks about what is on the screen in one step, typed or spoken; a provider without vision says so instead of answering a question it could not see; nothing new is stored, and no new destination is added.
- **Worse:** an Ollama owner with a vision model gets the refusal until the model's capabilities are read; a screenshot rides one stdin line, up to 3 MB decoded, next to a voice recording.
- **Neutral but noteworthy:** the vision values are fixed per provider, not per model; a model behind OpenAI or OpenRouter that cannot read images fails at the provider like any other bad request.

## Alternatives considered

- **Describe the image in text first (OCR) and send the text** - that is phase 5 of [[pages/roadmap/live-assistant]], with its own bundled tool; it is not a fallback for this phase.
- **Send the text alone when the provider cannot see, with a warning line** - the answer would read as if it had looked at the screen; refusing with one sentence is plainer.
- **Store the screenshot for later turns or for the history** - rejected; a later question sends a new capture, and nothing on the owner's screen is kept.
- **A separate port or channel for images** - rejected; the existing stdin line carries it ([[pages/decisions/0028-local-quick-ask-assistant]]).

## Enforcement

- `tests/Scout.Llm.Tests/ChatGatewayTests.cs:273` (image part of the user message), `tests/Scout.Llm.Tests/ChatGatewayAgentTests.cs:119` (agent path), `tests/Scout.Llm.Tests/ChatGatewayTests.cs:302` (no vision: refused before any call), `tests/Scout.Llm.Tests/MediaServicesRegistrationTests.cs:163` (per-provider Vision), `tests/Scout.Llm.Tests/Providers/ChatGptPlan/ChatGptPlanProviderTests.cs:133` (the `input_image` on the wire, composed through `AddScoutLlm`), `tests/Scout.Llm.Tests/ChatGatewayTargetLogTests.cs:126` (log flag without bytes).
- `tests/Scout.Conversation.Tests/Routing/LlmStreamingRouterTests.cs:202` (image reaches the request), `:249` (the sentence), `tests/Scout.Conversation.Tests/ConversationConductorLocalOwnerTests.cs:141` (voice keeps the image), `tests/Scout.Conversation.Tests/Abstractions/ChatMessageTests.cs:64` (size, never bytes, in `ToString`).
- `tests/Scout.Adapter.Local.Tests/LocalChatAdapterTests.cs:310` (text ask), `:334` (voice ask), `:364` (not a JPEG, empty, over 3 MB), `:387` (never in the log).
- `tests/Scout.ControlPlane.Tests/Features/FeatureCatalogTests.cs:145` (the desktop feature's screen, microphone and system audio reads and no write).
- Invariants: [[pages/invariants/pluggable-boundaries]], [[pages/invariants/no-executive-writes]].

## Related

- Services affected: [[pages/services/adapter-local]], [[pages/services/conversation]], [[pages/services/llm]], [[pages/services/control-plane]], [[pages/services/desktop-quick-ask]]
- Related decisions: [[pages/decisions/0028-local-quick-ask-assistant]], [[pages/decisions/0034-live-transcription-on-the-local-adapter]], [[pages/decisions/0032-per-feature-write-permissions]]
- Initiative: [[pages/roadmap/live-assistant]]
- The text of a screenshot can also be read locally, see [[pages/decisions/0036-text-read-from-a-screenshot-by-a-local-ocr-program]].
