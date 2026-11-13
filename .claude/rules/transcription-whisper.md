---
paths:
  - "src/Scout.Transcription.Whisper/**"
  - "tests/Scout.Transcription.Whisper.Tests/**"
  - "src/Scout.Host.Console/Scout.Host.Console.csproj"
---

# Transcription.Whisper

Owns Ogg Opus decoding to 16 kHz mono, reading 16 kHz mono WAV, cutting a live PCM16 stream into utterances, and running the Whisper model. Does not own when audio is transcribed, the size and duration limits, the download, or the reply ([[pages/services/conversation]]), nor the owner's recording, its sound taken out with ffmpeg and its limit of 60 minutes ([[pages/services/operator]]), nor downloading model files ([[pages/services/desktop-ui]]).

## Rules this project must keep

- [[pages/invariants/pluggable-boundaries]]: the core sees `IAudioTranscriber` and `ILiveTranscriber` only; no Whisper.net or Concentus type leaves this project.
- [[pages/invariants/no-executive-writes]]: reads a model file and a stream, writes nothing; live audio stays in memory.
- [[pages/invariants/local-first-default]]: transcription never calls a cloud service.

## Pages that govern this project

Read one when your change touches what its title names.

- [[pages/decisions/0024-voice-messages-transcribed-locally|ADR 0024 - Voice messages are transcribed locally, after the allow-list]]
- [[pages/decisions/0028-local-quick-ask-assistant|ADR 0028 - Quick ask: talk to Scout from the operating system, over the worker's stdio]] - WAV recordings from the quick-ask window
- [[pages/decisions/0034-live-transcription-on-the-local-adapter|ADR 0034 - Live transcription on the local adapter: PCM16 chunks in, partial and final text out]] - live transcription
- [[pages/invariants/pluggable-boundaries|Adapters, LLM providers and plugins are swapped by configuration]]
- [[pages/invariants/no-executive-writes|Scout performs descriptive writes only]]
- [[pages/services/conversation|Conversation]]
- [[pages/conventions/testing|Testing]]

## What to read for a change

Find the row your change matches and read that page. Read one or two rows, not the table.

| If the change touches | Read |
|---|---|
| Public contract | `context/pages/services/transcription-whisper/public-contract.md` |
| TranscribeAsync accepts audio/wav and audio/x-wav (src/Scout.Transcription.Whisper/WhisperAudioTranscriber.cs:272),; TranscribeSegmentsAsync reads a 16 kHz mono; Live transcription; A frame is speech when its; An... | `context/pages/services/transcription-whisper/how-it-works-transcribeasync-accepts-audio-wav-a.md` |
| Each window re-decodes the utterance from; Measured; The first partial's text got rougher; Publishing: Whisper.net loads its native libraries | `context/pages/services/transcription-whisper/how-it-works-each-window-re-decodes-the-utteranc.md` |
| Hot spots | `context/pages/services/transcription-whisper/hot-spots.md` |
| Testing footprint | `context/pages/services/transcription-whisper/testing-footprint.md` |

For where a type lives or who calls it, ask the code graph (`graphify explain "<Type>"`, `graphify affected "<Type>"`).
