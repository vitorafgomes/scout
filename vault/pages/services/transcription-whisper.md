---
title: "Transcription.Whisper"
version: "1.1"
date: 2026-10-28
updated: 2026-10-30
changes: "Also accepts 16 kHz mono WAV from the quick-ask window (ADR 0028)"
page_type: service
status: active
description: "Transcribes Ogg Opus voice messages and 16 kHz mono WAV recordings on this machine with a local Whisper ggml model through Whisper.net, decoding with the managed Concentus decoder; registered only when the configured model file exists."
source: ["src/Scout.Transcription.Whisper/", "tests/Scout.Transcription.Whisper.Tests/", "src/Scout.Host.Console/Scout.Host.Console.csproj"]
reliability: high
---

# Transcription.Whisper

> Turns a recorded voice message into text on the owner's machine, so Scout can answer it like a typed question.

## To change here, read

- [[pages/decisions/0024-voice-messages-transcribed-locally]]
- [[pages/decisions/0028-local-quick-ask-assistant]] - WAV recordings from the quick-ask window
- [[pages/invariants/pluggable-boundaries]]
- [[pages/invariants/no-executive-writes]]
- [[pages/services/conversation]]
- [[pages/conventions/testing]]

## Responsibility

Owns Ogg Opus decoding to 16 kHz mono, reading 16 kHz mono WAV, and running the Whisper model. Does not own when audio is transcribed, the size and duration limits, the download, or the reply ([[pages/services/conversation]]), nor downloading model files ([[pages/services/desktop-ui]]).

## Public contract

- **Entry point:** `AddWhisperTranscription(IServiceCollection, IConfiguration)` (`src/Scout.Transcription.Whisper/WhisperServiceCollectionExtensions.cs:19`) registers `IAudioTranscriber` only when `Scout:Transcription:ModelPath` names an existing file (`:27`); otherwise it registers nothing. Called by [[pages/services/host-console]].
- **Key types:** `WhisperAudioTranscriber : IAudioTranscriber, IDisposable` (`src/Scout.Transcription.Whisper/WhisperAudioTranscriber.cs:14`), internal `OggOpusDecoder` (`src/Scout.Transcription.Whisper/OggOpusDecoder.cs:14`), internal `WavDecoder` (`src/Scout.Transcription.Whisper/WavDecoder.cs:9`).
- **Configuration keys:** `Scout:Transcription:ModelPath`, a path to a ggml model file, not a credential. The desktop sets it to `ggml-large-v3-turbo.bin`, else `ggml-large-v3.bin`, under `userData/whisper-models`.
- **Packages:** `Whisper.net` 1.9.1 and `Whisper.net.Runtime` 1.9.1 (native whisper.cpp for linux, macOS and Windows), `Concentus` 2.2.2 and `Concentus.OggFile` 1.0.7 (managed Opus and Ogg).
- **External boundaries:** the model file on disk and the Whisper native library; no network.

## How it works

`TranscribeAsync` accepts `audio/wav` and `audio/x-wav` (`src/Scout.Transcription.Whisper/WhisperAudioTranscriber.cs:121`), `audio/ogg` and `audio/opus`, and throws `NotSupportedException` for anything else before the model loads (`src/Scout.Transcription.Whisper/WhisperAudioTranscriber.cs:53`). `WavDecoder.DecodeMono16k` walks the RIFF chunks to the `fmt ` and `data` chunks, skipping any others, and accepts only 16 kHz mono 16-bit PCM or 32-bit float, the extensible format included (`src/Scout.Transcription.Whisper/WavDecoder.cs:21`, `:68`); the samples go to Whisper as they are, with no Concentus and no resampling. It reads at most 5 minutes of samples (4,800,000, `:95`) and throws `InvalidDataException` for any other format, a file that is not WAV or one with no samples, which the conductor turns into its "could not transcribe" reply. `OggOpusDecoder.DecodeMono16k` decodes at Opus's native 48 kHz and averages every three samples down to 16 kHz (`src/Scout.Transcription.Whisper/OggOpusDecoder.cs:29`): the managed decoder returns silence when it decodes a SILK (voice-mode) stream straight to 16 kHz, which the committed fixture reproduces. Decoding stops after 5 minutes of audio (`:27`); a stream with no decodable audio throws `InvalidDataException` (`:45`). The model loads on the first call, not when the service is resolved (`src/Scout.Transcription.Whisper/WhisperAudioTranscriber.cs:63`); the processor detects the language, runs on half of the processors (at least one) so a local Ollama is not starved, and the segments are joined and trimmed (`:110`). A file that is not a ggml model throws `WhisperModelLoadException` when the processor is built, which the conductor turns into its "could not transcribe" reply. One transcription runs at a time behind a semaphore (`:60`). After each transcription a `TimeProvider` timer is re-armed for 10 minutes (`:70`); when it fires and no transcription holds the gate, the model is disposed (`:84`) and the next voice message loads it again. Loaded, large-v3-turbo holds about 1.6 GB and large-v3 about 3.1 GB. Model loading and transcription sit behind an internal `LoadedModel` seam so the gate and the idle unload are tested without a real model.

Publishing: Whisper.net loads its native libraries from `runtimes/<rid>/` beside the executable, which a single-file publish hides inside the bundle. A target in `src/Scout.Host.Console/Scout.Host.Console.csproj:39` keeps the target platform's Whisper natives as loose files next to the worker binary and drops the other platforms' copies; a single-file `linux-x64` publish then holds only `runtimes/linux-x64/` (about 4 MB), and the desktop packages the whole worker folder.

## Invariants

- [[pages/invariants/pluggable-boundaries]]: the core sees `IAudioTranscriber` only; no Whisper.net or Concentus type leaves this project.
- [[pages/invariants/no-executive-writes]]: reads a model file and a stream, writes nothing.
- [[pages/invariants/local-first-default]]: transcription never calls a cloud service.

## Hot spots

- `src/Scout.Transcription.Whisper/OggOpusDecoder.cs:29`: the 48 kHz decode rate is load-bearing; see above.
- `src/Scout.Transcription.Whisper/WhisperAudioTranscriber.cs:25`: one transcription at a time (ponytail); a pool sized to memory if voice traffic queues.

## Testing footprint

`tests/Scout.Transcription.Whisper.Tests` (15 test attributes, 18 test cases, 4 files; `WhisperWavTranscriptionTests.cs` builds WAV files in memory and checks, through the model seam, that 16-bit PCM and float samples reach the model, that other chunks are skipped, that 6 minutes are cut to exactly 5, and that 44.1 kHz, stereo and non-WAV bytes are invalid data before the model loads) with a committed fixture, `Fixtures/tone-440hz-1500ms.ogg` (4.8 KB, a 1.5 s 440 Hz tone encoded in Opus voice mode, generated with Concentus; no speech): the fixture decodes to about 24,000 non-silent samples, decoding stops at the maximum duration, non-Ogg bytes are invalid data, a non-Ogg content type is refused before the model loads, a file that is not a model fails with `WhisperModelLoadException` (this loads the native runtime), the registration gate, a second voice message that does not enter the model until the first releases it, and the idle unload and reload with a fake clock (`WhisperAudioTranscriberLifetimeTests.cs`). Speech recognition is not asserted by a test; a manual run with `ggml-tiny.bin` on linux-x64 (AVX2) on 2026-10-28 ran inference in about 1.5 s and returned an empty transcript for the tone; the same run from a single-file `linux-x64` publish of a probe with the natives kept beside the binary also worked, while without that the loader reported "Native Library not found in default paths".

## Related

Direct project references: [[pages/services/conversation]]. Referenced by [[pages/services/host-console]]; its WAV input comes from [[pages/services/adapter-local]]. Map: [[pages/architecture/project-map]].
