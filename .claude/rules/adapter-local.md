---
paths:
  - "src/Scout.Adapter.Local/**"
  - "tests/Scout.Adapter.Local.Tests/**"
  - "src/Scout.Host.Console/HostComposition.cs"
---

# Adapter.Local

Owns reading requests from stdin, turning each into a local-owner `ChatMessage`, cancelling a request, running up to two live transcription sessions at once, answering a read with the text on a screenshot, turning the four meeting requests into calls on the meetings store, answering a speak with the audio of one sentence, and writing reply events to stdout. Does not own routing, memory, transcription or the access rules ([[pages/services/conversation]]), nor the meetings database or the Markdown document ([[pages/services/meetings]]), nor the quick-ask window, the shortcut or the microphone ([[pages/services/desktop-quick-ask]]).

## Rules this project must keep

- [[pages/invariants/no-executive-writes]]: the only writes are the reply events to stdout, which the desktop shows in the owner's quick-ask window and Chat screen, the live `partial`, `final`, `translation` and `complete` events, and the later `notice` events, which the desktop drops until it learns the kind. Live audio, an attached screenshot and the text read from one stay in memory. A meeting is written only by the store, to Scout's own data folder, and only on a `keep` ([[pages/services/meetings]]). The adapter adds no tool the model can call, and a `read`, a `speak` or a meeting request comes only from the desktop's stdin; a `speak` writes only its `audio` event, and the local voice's one temporary file is the voice's own ([[pages/services/operator]]).
- [[pages/invariants/pluggable-boundaries]]: references only `Scout.Conversation` and Microsoft.Extensions abstractions. No vendor SDK. Live transcription arrives as `ILiveTranscriber`, never a Whisper type, and the screenshot text as `IScreenTextReader`, never a tesseract type; a translation goes through `ILiveTranslator`, never a Semantic Kernel type; a meeting goes through `IMeetingStore` and its Markdown through a `Func<MeetingRecord, string>`, both over Conversation records, never a Sqlite type, and the project references no `Scout.Meetings`; the voice arrives as `ISpeechSynthesizer` and a `SpeechAudio` of a content type and bytes, never a Semantic Kernel or Piper type.
- [[pages/invariants/secrets-never-in-source]]: no credential. Request, reply, transcript, translation and screenshot text, screenshot bytes, meeting text and a sentence read aloud are never logged.

## Pages that govern this project

Read one when your change touches what its title names.

- [[pages/decisions/0028-local-quick-ask-assistant|ADR 0028 - Quick ask: talk to Scout from the operating system, over the worker's stdio]]
- [[pages/decisions/0034-live-transcription-on-the-local-adapter|ADR 0034 - Live transcription on the local adapter: PCM16 chunks in, partial and final text out]] - live transcription sessions
- [[pages/decisions/0035-screenshot-attached-to-a-question|ADR 0035 - A screenshot attached to a question: one JPEG on the ask line, an image part for a provider that can see, a plain sentence otherwise]] - a screenshot attached to an ask
- [[pages/decisions/0036-text-read-from-a-screenshot-by-a-local-ocr-program|ADR 0036 - Text read from a screenshot by a local OCR program: the bundled tesseract on the JPEG bytes, through standard input and output]] - the text read from a screenshot
- [[pages/decisions/0037-meetings-kept-locally-in-sqlite|ADR 0037 - Meetings kept locally in SQLite: only when the owner keeps one, under Scout's data folder, deleted with VACUUM]] - the meetings the owner keeps
- [[pages/invariants/no-executive-writes|Scout performs descriptive writes only]]
- [[pages/invariants/pluggable-boundaries|Adapters, LLM providers and plugins are swapped by configuration]]
- [[pages/decisions/0021-allowed-users|ADR 0021 - Only allowed users can use Scout once an owner credential is connected]] - why the local owner skips the allow-list
- [[pages/services/conversation|Conversation]]
- [[pages/conventions/testing|Testing]]

## What to read for a change

Find the row your change matches and read that page. Read one or two rows, not the table.

| If the change touches | Read |
|---|---|
| Public contract | `context/pages/services/adapter-local/public-contract.md` |
| StartAsync opens stdin and starts a; DispatchAsync (src/Scout.Adapter.Local/LocalChatAdapter.cs:295) handles each line, and; An oversized line gets fail That; Malformed JSON, or a request with; cancel cancels the... | `context/pages/services/adapter-local/how-it-works-startasync-opens-stdin-and-starts-a.md` |
| One translation runs at a time; Context is the two finals spoken; At most 20 translations start in; Each call has 20 s (src/Scout.Adapter.Local/LiveTranslationQueue.cs:21).; Without an ILiveTranslator the listen is;... | `context/pages/services/adapter-local/how-it-works-one-translation-runs-at-a-time.md` |
| Hot spots | `context/pages/services/adapter-local/hot-spots.md` |
| Testing footprint | `context/pages/services/adapter-local/testing-footprint.md` |

For where a type lives or who calls it, ask the code graph (`graphify explain "<Type>"`, `graphify affected "<Type>"`).
