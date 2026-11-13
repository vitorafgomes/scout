---
title: "Transcription.Whisper - history"
version: "1.0"
date: 2026-11-08
changes: "Initial history note: live quiet relative to the room, and the turn-end log line"
page_type: reference
status: artefact
description: "What changed in local Whisper transcription and when."
source:
  - chat
  - src/Scout.Transcription.Whisper/LiveUtteranceSegmenter.cs
  - src/Scout.Transcription.Whisper/WhisperLiveTranscriber.cs
  - tests/Scout.Transcription.Whisper.Tests/LiveUtteranceSegmenterTests.cs
reliability: high
---

# Transcription.Whisper - history

> Dated changes to [[pages/services/transcription-whisper]]; the service page states how it works now.

## 2026-11-08 - Quiet relative to the room

- **Asked for:** the owner, translated: "Conversation still does not work: I enabled it, spoke, and nothing was captured. No error in the logs." ([[pages/decisions/0050-read-aloud-and-conversation-mode]], amendment "a conversation that hears in a noisy room").
- **What changed:** a live turn ended only after 1 s below a fixed RMS of 0.015, so a room at or above it never ended a turn before the 20 s cut; reproduced with speech followed by 15 s of a room at RMS 0.02, which gave no turn end. A frame is now speech only when it is at least 0.015 and three times the quietest frame of the last 3 s; digital silence is not the room; a turn begun in the first 3 s, before the room is learned, is kept only with 160 ms of speech for the room known when it ends. The same reproduction now ends the turn 4 s in, and 1 s after the speech when the room was heard before it. Each turn end carries Quiet, Cap or Flush, and the live transcriber logs it at Information with its length, never its text. A session that ends without any turn logs its loudest frame RMS and its room, so a quiet or unselected microphone is visible. A turn begun in the first 3 s makes a window ready only once it holds 160 ms three times the quietest non-zero frame heard so far, so noise shows no partial and speech with dips between syllables still does.
- **Tests:** six new segmenter tests (a room at 0.02, 0.035 and 0.05, a room alone, two words kept whole, a rising room, a muted microphone, the reason of each end), the log line in `WhisperLiveTranscriberTests` and in the composed registration, the no-speech line in en-GB, pt-BR and for digital silence; noise making no window and syllables making theirs in the first 3 s; eight window tests that feed a constant tone from the first frame now feed one 20 ms frame of quiet room first, with their assertions unchanged; `An_Utterance_Is_Closed_At_20_Seconds_And_A_New_One_Starts` changed its waveform phase, assertions unchanged: its sawtooth starts half a period in, because with the old phase the cut landed on the trough, now quiet relative to the room the sawtooth sets, and the second turn came back 77,440 samples long instead of 80,000. Later the same day the room was capped at 0.05 for the threshold, so music cannot set a room that hides softer speech; counting a zero frame as 0.005 was rejected because it breaks the muted-microphone test. Two tests cover the cap: softer speech after music and 4 s of digital silence, and speech right after music.

## Version notes: `pages/services/transcription-whisper.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- A speech runtime that cannot load (on Linux, libgomp missing) fails with the vendor-free TranscriptionUnavailableException and a sentence the owner can act on, the loader's error kept as its inner exception for the logs; a voice message gets its own reply; the live tests of a failed load run on the model seam, and the model-load test asserts one exact outcome per capability (2026-11-12, issue 372). Earlier: Quiet relative to the room (2026-11-08): a live frame is speech only when its RMS is at least 0.015 and three times the quietest frame of the last 3 s, digital silence is not the room, a turn begun before the room is learned is kept only with enough speech for it; each turn end carries why it ended and is logged at Information without its text; a session with no turn logs its loudest frame and room; a turn begun before the room is learned makes a window ready only with 160 ms three times the quietest frame heard so far. Earlier: Merge of the channel operator and the live assistant. Channel operator: Phase 8b review: the WAV decoded through a pooled 64 KB buffer, a voice message waits at most 30 seconds behind a recording and is refused as busy, the processor released with DisposeAsync; references re-pointed by content and the test footprint recounted. A long recording is not timed yet, and why. TranscribeSegmentsAsync: the timed segments of a 16 kHz mono WAV recording up to the caller's limit, for the YouTube operator's own video, behind the same gate held for the whole enumeration; the model seam yields segments; references into WhisperAudioTranscriber.cs re-pointed by content. Also accepts 16 kHz mono WAV from the quick-ask window (ADR 0028). Live assistant: Live transcription: an RMS utterance segmenter, greedy partials over a per-window audio context and beam-5 finals over the shared model (ADR 0034); measured latencies.
