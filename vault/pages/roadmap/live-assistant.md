---
title: "Live assistant: hear, see and translate for the owner"
version: "1.0"
date: 2026-11-04
changes: "Initial initiative page"
page_type: roadmap
status: stub
description: "Initiative: Scout hears a call or meeting, shows the owner a live transcript the chat can answer about, captures the screen on request, reads its text and translates, all on the owner's machine, declared as reads under ADR 0032."
source:
  - chat
reliability: medium
---

# Live assistant: hear, see and translate for the owner

> The owner at the desktop can let Scout listen to a call or meeting, see the transcript as it forms, attach a screenshot to a question, and read both in another language; every capture is visible, consented through the system, declared as a read and local first.

## Why now

The owner asked for four things in one sitting: that Scout hear what is said in a call or meeting, that the chat be able to see what was said, that Scout capture what is on the screen, and that it translate. Scout has the pieces for part of this and not the rest: a local Whisper engine that transcribes one finished recording ([[pages/services/transcription-whisper]]), a quick-ask window with push-to-talk ([[pages/services/desktop-quick-ask]]), a per-feature declaration of reads and writes ([[pages/decisions/0032-per-feature-write-permissions]]) and a bundling pattern for pinned native tools ([[pages/services/desktop-operator]], Media tools). What is missing is capture of the screen and of system audio, a streaming transcript, and the translation and meeting layers on top.

## Users

The owner, at the desktop, on Linux first (Wayland under COSMIC included), macOS and Windows after. Nobody else is recorded on purpose: capture starts only when the owner starts it and stops when the owner stops it.

## Scope

- **In scope:** streaming transcription of the microphone and of system audio; an on-demand screenshot attached to a question; text extraction from an attached screenshot; translation of a transcript; a meeting session that gives the chat the recent transcript, the screenshot text and the last answer; the desktop surfaces for consent, indicator, preview and export.
- **Out of scope and non-goals:**
  - Hidden capture: no capture without a visible indicator, no capture that starts by itself, nothing periodic for images.
  - Stealth: no content protection on Scout's windows to keep them out of recordings or screen sharing, no hiding from the taskbar or the window list to avoid notice.
  - Background monitoring of the clipboard or the text selection; at most one read the owner asks for.
  - Cloud transcription: transcription stays on the machine ([[pages/invariants/local-first-default]], [[pages/decisions/0024-voice-messages-transcribed-locally]]).
  - A virtual audio driver, an administrator prompt, or a subprocess for transcription.
  - Storing a recording; audio is transcribed and dropped.
- **Capture rules (all phases):**
  - Each capture is a feature row declared as a read of the owner's own machine, and the owner turns it on; no write is added ([[pages/decisions/0032-per-feature-write-permissions]]).
  - A persistent indicator is visible while audio or screen capture runs.
  - On Wayland consent goes through the system portal, never around it.
  - An image or a transcript goes to the connected provider only as part of a turn the owner sent ([[pages/invariants/pluggable-boundaries]], one chosen provider).

## Target outcome

By the end of phase 4, the owner starts a call, turns on "Listen to this computer", watches the transcript form in the desktop, asks the chat "what did they just ask me?" and gets an answer from the recent transcript, and reads the transcript beside a translation, with nothing leaving the machine except the turns sent to the provider the owner connected.

## Status

- **As of 2026-11-04:** not started. Nothing in this page is built; the gap table below is what Scout does today.
- **Next milestone:** the owner picks which capture to build first (Decisions pending); phase 1 or phase 2 starts from that choice.
- **Blocker:** none technical; phases 3 and 6 need the owner's choice on persistence and phase 4 on the target language.

## Gap today

| Capability | Scout today | Phase |
|---|---|---|
| Live transcription | `Scout.Transcription.Whisper` transcribes one 16 kHz mono WAV or Ogg Opus per turn through Whisper.net, with a 5 minute cap and no partial text ([[pages/services/transcription-whisper]]) | 1 |
| Model choice and integrity | Main downloads `ggml-large-v3-turbo.bin`, else `ggml-large-v3.bin`, into `userData/whisper-models`; no pinned size or hash | 1 |
| Microphone | `WavRecorder`: 16 kHz, `ScriptProcessorNode` (the CSP blocks a blob-URL `AudioWorklet`), push-to-talk, gain 0, 5 minute cap; `media` granted to the app's own page for audio only ([[pages/services/desktop-quick-ask]]) | kept; 3 adds echo handling |
| Screenshot | none | 2 |
| System audio | none | 3 |
| Live transcript in the desktop | none; turns are text or one voice message | 3 |
| Translation | none | 4 |
| Text from an image | none | 5 |
| Image into a turn | the local adapter carries text and a WAV attachment only ([[pages/services/adapter-local]]) | 2 |
| Meeting context | quick-ask turns, cancel, the last 200 turns in memory | 6 |
| Selection and clipboard | none | not planned beyond one read on request |

## Phases

### Phase 1 - Streaming transcription

- **Scope:** a streaming session in `Scout.Transcription.Whisper`. The desktop sends PCM16 mono 16 kHz chunks over the local adapter; the worker cuts 0.75 s windows with 0.15 s overlap, runs a voice activity check on RMS energy (a threshold, 1 s of silence ends an utterance, 160 ms minimum speech), keeps a 20 s ring buffer (oldest dropped), emits partial text per window and, when an utterance ends, a final text re-decoded at beam 5. The numbers are a starting point to tune on real calls. Models offered: tiny, base, small and large-v3-turbo, each pinned by size and SHA-256 and verified before use, the way the media tools are pinned and fetched ([[pages/services/desktop-operator]]). The engine stays in-process through Whisper.net; no subprocess.
- **Acceptance (what the owner sees):** holding or toggling the microphone shows words appearing within about a second, then settling into a final line; choosing a smaller model in Settings makes it faster; a model file with a wrong hash is refused with a sentence, not loaded.
- **Invariants touched:** [[pages/invariants/local-first-default]] (no cloud), [[pages/invariants/pluggable-boundaries]] (no Whisper.net type leaves the project), [[pages/invariants/no-executive-writes]] (writes nothing outside its model folder).
- **Effort:** M.
- **Open decisions:** the wire shape of a streamed chunk and of partial and final events on the local adapter; whether the 5 minute cap stays for push-to-talk.

### Phase 2 - Screenshot on demand

- **Scope:** an "Attach a screenshot" action in the quick-ask window. Main answers the renderer's display-media request with Electron's display-media request handler and the system picker (the desktop portal on Wayland, the PipeWire capturer left on). The frame is scaled to at most 1920x1080 and encoded as JPEG at about quality 80; the owner sees a preview and can discard it before sending. The local adapter gains an image attachment, and the turn passes it to the connected provider as an image it can read. Declared as a read; nothing periodic, no shortcut that captures without the picker.
- **Acceptance:** the owner presses Attach, picks a screen or window in the system dialog, sees the image, sends it with a question, and the answer refers to what is on it; cancelling the dialog attaches nothing.
- **Invariants touched:** [[pages/decisions/0032-per-feature-write-permissions]] (new declared read), [[pages/invariants/pluggable-boundaries]] (the image reaches the model through Semantic Kernel only), [[pages/invariants/secrets-never-in-source]] (nothing logs the image).
- **Effort:** M.
- **Open decisions:** what happens on a provider with no image input (refuse with a sentence, or fall back to phase 5 text); the size limit of an image on the stdio transport.

### Phase 3 - System audio and a live transcript panel

- **Scope:** loopback capture of what the computer plays, through Electron's display-media handler with audio loopback enabled (PulseAudio and PipeWire-pulse on Linux), after portal consent. A persistent recording indicator stays on Scout's own window while it runs. When microphone and loopback run together, the loopback is the reference for app-side echo cancellation; with the microphone alone nothing changes. A live transcript panel in the desktop shows the stream from phase 1, labelled me and them, and the chat can be asked about it. The audio is never written to disk.
- **Acceptance:** the owner joins a call, turns on "Listen to this computer", picks the source in the system dialog, sees the indicator and the transcript, stops it with one click and the indicator goes away; asking the chat "summarise the last five minutes" answers from the transcript.
- **Invariants touched:** ADR 0032 (declared read with a visible indicator), [[pages/invariants/local-first-default]], the desktop CSP (any new capture path must work inside it; see [[pages/services/desktop-ui]]).
- **Effort:** M.
- **Open decisions:** how the chat reaches the transcript (a tool the model calls, or context added to the turn); behaviour when the portal is unavailable.

### Phase 4 - Translation

- **Scope:** Whisper's own translate mode produces English only, so it covers one target. Any other target language is produced by the connected LLM over each transcript window, incrementally, and shown beside the original. With Ollama connected this works offline ([[pages/invariants/local-first-default]]).
- **Acceptance:** the owner picks a target language; each finished line shows its translation next to it within a few seconds; switching the target applies to new lines.
- **Invariants touched:** [[pages/invariants/pluggable-boundaries]] (translation through the one LLM path), one chosen provider (the transcript leaves the machine only if the connected provider is remote, and the panel says so).
- **Effort:** S.
- **Open decisions:** whether the target defaults to the mandate language of the channel operator (Decisions pending).

### Phase 5 - Text from a screenshot

- **Scope:** a bundled tesseract in the worker, pinned by SHA-256 and fetched at build time like ffmpeg and piper, run on an attached screenshot only: page segmentation mode 6, a 1 s budget, a 5000-character cap. The text is shown to the owner before it is sent with the turn.
- **Acceptance:** after Attach, the owner sees the image and the extracted text, can edit or drop the text, and sends; a slow or failing run leaves the image alone and says why.
- **Invariants touched:** [[pages/decisions/0032-per-feature-write-permissions]] (a subprocess is declared; a failing-call test applies, Rule I), the no-run-time-download rule of the media tools.
- **Effort:** M.
- **Open decisions:** bundle the tool or wrap a managed library; language data beyond English.

### Phase 6 - Meeting session

- **Scope:** a session in the worker that, while listening, builds the context for each turn from the recent transcript windows, the screenshot text and the last answer, size-limited. One request is in flight; a newer one supersedes it, and the owner can cancel. Timers by mode run from 10 to 90 s, only while the owner has started the session. Persistence is opt-in: a Scout-owned schema, with export and delete; by default nothing is stored.
- **Acceptance:** the owner starts a session in a mode, gets an answer or suggestion at that cadence without asking, stops it, and finds nothing on disk unless persistence was turned on; export writes a file the owner chooses, delete removes the session.
- **Invariants touched:** ADR 0032 (declared read, and a write to Scout's own folder only when persistence is on), [[pages/invariants/no-executive-writes]], the conversation memory ([[pages/services/conversation]]).
- **Effort:** L.
- **Open decisions:** the size budget of the context; whether timer-driven answers are allowed at all or only owner-triggered ones.

## Decisions pending

For the owner:

- Which capture to build first: streaming transcription (phase 1), the screenshot (phase 2) or system audio (phase 3). Phase 2 is the smallest visible win; phase 1 is the base for 3, 4 and 6.
- Whether translation targets the channel mandate's language by default or a language chosen in the live panel.
- Whether the meeting session stores anything by default. Proposed answer: no; persistence is a per-session opt-in with export and delete.
- Whether timer-driven answers in a meeting session are wanted, or only answers the owner asks for.

ADRs to write when a phase starts: capture features and their indicator under ADR 0032 (new), and the streaming wire shape on the local adapter (amends [[pages/decisions/0028-local-quick-ask-assistant]]).

## Services affected

- [[pages/services/transcription-whisper]]
- [[pages/services/adapter-local]]
- [[pages/services/desktop-ui]]
- [[pages/services/desktop-quick-ask]]
- [[pages/services/desktop-operator]] (tool bundling pattern)
- [[pages/services/conversation]]

## Risks

- **Risk:** the portal dialog or the loopback source differs by compositor and Chromium version - **mitigation:** test on Wayland and X11, keep the PipeWire capturer on, and say in a sentence when the system refuses.
- **Risk:** the quick-ask window forces X11 under Wayland today, which can change what the picker offers - **mitigation:** check the picker under that relaunch before phase 2 is built.
- **Risk:** other people's voices are transcribed - **mitigation:** visible indicator, nothing stored by default, and the owner is the one who starts it.
- **Risk:** a screenshot or transcript reaches a remote provider by accident - **mitigation:** only inside a turn the owner sent; the panel names the connected provider.
- **Risk:** streaming load starves the worker - **mitigation:** one stream at a time, bounded ring buffer, drop windows rather than queue.

## History

None yet.

## Related

- ADRs: [[pages/decisions/0032-per-feature-write-permissions]], [[pages/decisions/0028-local-quick-ask-assistant]], [[pages/decisions/0024-voice-messages-transcribed-locally]], [[pages/decisions/0004-web-stack-angular]], [[pages/decisions/0011-backend-frontend-grpc-uds]]
- Invariants: [[pages/invariants/local-first-default]], [[pages/invariants/no-executive-writes]], [[pages/invariants/pluggable-boundaries]]
- Initiatives: [[pages/roadmap/control-plane-client]], [[pages/roadmap/youtube-channel-operator]]
