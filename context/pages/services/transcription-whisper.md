---
title: "Transcription.Whisper"
version: "1.6"
date: 2026-10-28
updated: 2026-11-12
changes: "A speech runtime that cannot load (on Linux, libgomp missing) fails with the vendor-free TranscriptionUnavailableException and a sentence the owner can act on, the loader's error kept as its inner exception for the logs; a voice message gets its own reply; the live tests of a failed load run on the."
page_type: service
status: active
description: "Transcribes Ogg Opus voice messages, 16 kHz mono WAV recordings and a live 16 kHz PCM16 stream on this machine with one local Whisper ggml model through Whisper.net, decoding Opus with the managed Concentus decoder, and yields the timed segments of a WAV recording up to the caller's limit for the YouTube operator; registered only when the configured model file exists."
source: ["src/Scout.Transcription.Whisper/", "tests/Scout.Transcription.Whisper.Tests/", "src/Scout.Host.Console/Scout.Host.Console.csproj"]
reliability: high
---

# Transcription.Whisper

> Turns a recorded voice message into text on the owner's machine, so Scout can answer it like a typed question, and turns live speech into text while it is spoken.

## Responsibility

Owns Ogg Opus decoding to 16 kHz mono, reading 16 kHz mono WAV, cutting a live PCM16 stream into utterances, and running the Whisper model. Does not own when audio is transcribed, the size and duration limits, the download, or the reply ([[pages/services/conversation]]), nor the owner's recording, its sound taken out with ffmpeg and its limit of 60 minutes ([[pages/services/operator]]), nor downloading model files ([[pages/services/desktop-ui]]).

## Rules

The rules this project must keep, and what to read before changing it, are in `.claude/rules/transcription-whisper.md`. Claude Code loads that file when a task touches the project.

## Topics

Read the page your change touches, not all of them.

| Page | Read when a change touches | Characters |
|---|---|---|
| [[pages/services/transcription-whisper/public-contract\|Public contract]] | Public contract | 2141 |
| [[pages/services/transcription-whisper/how-it-works-transcribeasync-accepts-audio-wav-a\|How it works: TranscribeAsync accepts audio/wav and audio/x-wav (src/Scout.Transcription.Whisper/WhisperAudioTranscriber.cs:272),]] | TranscribeAsync accepts audio/wav and audio/x-wav (src/Scout.Transcription.Whisper/WhisperAudioTranscriber.cs:272),; TranscribeSegmentsAsync reads a 16 kHz mono; Live transcription; A frame is speech when its; An... | 12448 |
| [[pages/services/transcription-whisper/how-it-works-each-window-re-decodes-the-utteranc\|How it works: Each window re-decodes the utterance from]] | Each window re-decodes the utterance from; Measured; The first partial's text got rougher; Publishing: Whisper.net loads its native libraries | 2835 |
| [[pages/services/transcription-whisper/hot-spots\|Hot spots]] | Hot spots | 976 |
| [[pages/services/transcription-whisper/testing-footprint\|Testing footprint]] | Testing footprint | 6359 |

## Related

Direct project references: [[pages/services/conversation]]. Referenced by [[pages/services/host-console]]; its WAV input comes from [[pages/services/adapter-local]]. Map: [[pages/architecture/project-map]].

Streaming transcription of live audio is planned in [[pages/roadmap/live-assistant]].
