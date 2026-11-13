---
title: "Transcription.Whisper: Hot spots"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Transcription.Whisper service page."
page_type: service
status: active
description: "Read when a change touches: Hot spots."
source: ["src/Scout.Transcription.Whisper/", "tests/Scout.Transcription.Whisper.Tests/", "src/Scout.Host.Console/Scout.Host.Console.csproj"]
reliability: high
---

## Hot spots

- `src/Scout.Transcription.Whisper/OggOpusDecoder.cs:29`: the 48 kHz decode rate is load-bearing; see above.
- `src/Scout.Transcription.Whisper/WhisperAudioTranscriber.cs:32`: one transcription at a time (ponytail); a pool sized to memory if voice traffic queues. A recording of the owner's own video holds that gate for its whole transcription, minutes for a long one; a voice message waits at most 30 seconds behind it and gets the busy sentence. Transcribing in 5-minute windows, releasing the gate between them, is the upgrade path in [[pages/roadmap/followups]].
