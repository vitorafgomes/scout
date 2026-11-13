---
paths:
  - "src/Scout.Conversation/**"
  - "tests/Scout.Conversation.Tests/**"
---

# Conversation

Owns the adapter contract, message and response shapes, routing, per-channel memory, the streaming edit loop and the voice-message flow (limits, download through the adapter's delegate, transcription through `IAudioTranscriber`). Does not own a speech model ([[pages/services/transcription-whisper]]) nor platform SDK calls ([[pages/services/adapter-discord]], [[pages/services/adapter-slack]], [[pages/services/adapter-teams]]) nor model access ([[pages/services/llm]]).

## Rules this project must keep

- [[pages/invariants/pluggable-boundaries]]: this project is the seam the adapters and the host plug into.
- [[pages/invariants/no-executive-writes]]: the only write it performs is the reply through the sink; the voice download it triggers is a read through the adapter's delegate.

## Pages that govern this project

Read one when your change touches what its title names.

- [[pages/invariants/pluggable-boundaries|Adapters, LLM providers and plugins are swapped by configuration]]
- [[pages/invariants/no-executive-writes|Scout performs descriptive writes only]]
- [[pages/decisions/0021-allowed-users|ADR 0021 - Only allowed users can use Scout once an owner credential is connected]] - who may use Scout at all
- [[pages/decisions/0024-voice-messages-transcribed-locally|ADR 0024 - Voice messages are transcribed locally, after the allow-list]] - voice messages
- [[pages/decisions/0028-local-quick-ask-assistant|ADR 0028 - Quick ask: talk to Scout from the operating system, over the worker's stdio]] - the local owner from the quick-ask window
- [[pages/decisions/0034-live-transcription-on-the-local-adapter|ADR 0034 - Live transcription on the local adapter: PCM16 chunks in, partial and final text out]] - the live transcription abstraction
- [[pages/decisions/0035-screenshot-attached-to-a-question|ADR 0035 - A screenshot attached to a question: one JPEG on the ask line, an image part for a provider that can see, a plain sentence otherwise]] - a picture attached to a message
- [[pages/decisions/0036-text-read-from-a-screenshot-by-a-local-ocr-program|ADR 0036 - Text read from a screenshot by a local OCR program: the bundled tesseract on the JPEG bytes, through standard input and output]] - the screenshot text reader interface
- [[pages/decisions/0037-meetings-kept-locally-in-sqlite|ADR 0037 - Meetings kept locally in SQLite: only when the owner keeps one, under Scout's data folder, deleted with VACUUM]] - the meetings store interface; implemented by [[pages/services/meetings|Meetings]]
- [[pages/decisions/0033-channel-operator-architecture|ADR 0033 - Channel operator architecture: state in the control plane, pipeline in Scout.Operator, media through the connected AI Source, progress back to the originating channel]] - progress posted later into the originating conversation
- [[pages/services/llm|Llm]]
- [[pages/services/skills|Skills]]
- [[pages/conventions/testing|Testing]]

## What to read for a change

Find the row your change matches and read that page. Read one or two rows, not the table.

| If the change touches | Read |
|---|---|
| Public contract | `context/pages/services/conversation/public-contract.md` |
| mermaid; Route defers all work to enumeration; Functions per turn; Local owner; Pairing; Allowed users; Progress before the text | `context/pages/services/conversation/how-it-works-mermaid.md` |
| Blocks a function hands to the reply; Voice messages; The desktop dictates; Voice message without a model, to the desktop; Origin and later posts; Sign-in required; No text from the model; Rejected key | `context/pages/services/conversation/how-it-works-blocks-a-function-hands-to-the-repl.md` |
| Model not found; Quota or usage limit; A picture on the message; Error endings within the length limit; The overall timeout and the terminal write | `context/pages/services/conversation/how-it-works-model-not-found.md` |
| Hot spots | `context/pages/services/conversation/hot-spots.md` |
| Testing footprint | `context/pages/services/conversation/testing-footprint.md` |

For where a type lives or who calls it, ask the code graph (`graphify explain "<Type>"`, `graphify affected "<Type>"`).
