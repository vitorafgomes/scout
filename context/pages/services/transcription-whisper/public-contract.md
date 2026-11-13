---
title: "Transcription.Whisper: Public contract"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Transcription.Whisper service page."
page_type: service
status: active
description: "Read when a change touches: Public contract."
source: ["src/Scout.Transcription.Whisper/", "tests/Scout.Transcription.Whisper.Tests/", "src/Scout.Host.Console/Scout.Host.Console.csproj"]
reliability: high
---

## Public contract

- **Entry point:** `AddWhisperTranscription(IServiceCollection, IConfiguration)` (`src/Scout.Transcription.Whisper/WhisperServiceCollectionExtensions.cs:21`) registers one `WhisperAudioTranscriber` singleton as `IAudioTranscriber`, and `ILiveTranscriber` over that same instance with the host's `ILogger<WhisperLiveTranscriber>` when there is one (`:38`), only when `Scout:Transcription:ModelPath` names an existing file (`:29`); otherwise it registers nothing. Called by [[pages/services/host-console]].
- **Key types:**
  - `WhisperAudioTranscriber : IAudioTranscriber, IDisposable` (`src/Scout.Transcription.Whisper/WhisperAudioTranscriber.cs:17`), with `TranscribeAsync` for a voice message and `TranscribeSegmentsAsync(wav, maxDuration, ct)` (`src/Scout.Transcription.Whisper/WhisperAudioTranscriber.cs:77`) for a recording.
  - Internal `WhisperLiveTranscriber : ILiveTranscriber` (`src/Scout.Transcription.Whisper/WhisperLiveTranscriber.cs:18`).
  - Internal `LiveUtteranceSegmenter` (`src/Scout.Transcription.Whisper/LiveUtteranceSegmenter.cs:19`).
  - Internal `OggOpusDecoder` (`src/Scout.Transcription.Whisper/OggOpusDecoder.cs:14`) and `WavDecoder` (`src/Scout.Transcription.Whisper/WavDecoder.cs:10`).
- **Configuration keys:** `Scout:Transcription:ModelPath`, a path to a ggml model file, not a credential. The desktop sets it to `ggml-large-v3-turbo.bin`, else `ggml-large-v3.bin`, under `userData/whisper-models`.
- **Packages:** `Whisper.net` 1.9.1 and `Whisper.net.Runtime` 1.9.1 (native whisper.cpp for linux, macOS and Windows), `Concentus` 2.2.2 and `Concentus.OggFile` 1.0.7 (managed Opus and Ogg).
- **External boundaries:** the model file on disk and the Whisper native library; no network.
