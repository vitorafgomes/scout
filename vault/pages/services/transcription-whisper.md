---
title: "Transcription.Whisper"
version: "1.4"
date: 2026-10-28
updated: 2026-11-04
changes: "Merge of the channel operator and the live assistant. Channel operator: Phase 8b review: the WAV decoded through a pooled 64 KB buffer, a voice message waits at most 30 seconds behind a recording and is refused as busy, the processor released with DisposeAsync; references re-pointed by content and the test footprint recounted. A long recording is not timed yet, and why. TranscribeSegmentsAsync: the timed segments of a 16 kHz mono WAV recording up to the caller's limit, for the YouTube operator's own video, behind the same gate held for the whole enumeration; the model seam yields segments; references into WhisperAudioTranscriber.cs re-pointed by content. Also accepts 16 kHz mono WAV from the quick-ask window (ADR 0028). Live assistant: Live transcription: an RMS utterance segmenter, greedy partials over a per-window audio context and beam-5 finals over the shared model (ADR 0034); measured latencies."
page_type: service
status: active
description: "Transcribes Ogg Opus voice messages, 16 kHz mono WAV recordings and a live 16 kHz PCM16 stream on this machine with one local Whisper ggml model through Whisper.net, decoding Opus with the managed Concentus decoder, and yields the timed segments of a WAV recording up to the caller's limit for the YouTube operator; registered only when the configured model file exists."
source: ["src/Scout.Transcription.Whisper/", "tests/Scout.Transcription.Whisper.Tests/", "src/Scout.Host.Console/Scout.Host.Console.csproj"]
reliability: high
---

# Transcription.Whisper

> Turns a recorded voice message into text on the owner's machine, so Scout can answer it like a typed question, and turns live speech into text while it is spoken.

## To change here, read

- [[pages/decisions/0024-voice-messages-transcribed-locally]]
- [[pages/decisions/0028-local-quick-ask-assistant]] - WAV recordings from the quick-ask window
- [[pages/decisions/0034-live-transcription-on-the-local-adapter]] - live transcription
- [[pages/invariants/pluggable-boundaries]]
- [[pages/invariants/no-executive-writes]]
- [[pages/services/conversation]]
- [[pages/conventions/testing]]

## Responsibility

Owns Ogg Opus decoding to 16 kHz mono, reading 16 kHz mono WAV, cutting a live PCM16 stream into utterances, and running the Whisper model. Does not own when audio is transcribed, the size and duration limits, the download, or the reply ([[pages/services/conversation]]), nor the owner's recording, its sound taken out with ffmpeg and its limit of 60 minutes ([[pages/services/operator]]), nor downloading model files ([[pages/services/desktop-ui]]).

## Public contract

- **Entry point:** `AddWhisperTranscription(IServiceCollection, IConfiguration)` (`src/Scout.Transcription.Whisper/WhisperServiceCollectionExtensions.cs:20`) registers one `WhisperAudioTranscriber` singleton as `IAudioTranscriber`, and `ILiveTranscriber` over that same instance, only when `Scout:Transcription:ModelPath` names an existing file (`:28`); otherwise it registers nothing. Called by [[pages/services/host-console]].
- **Key types:**
  - `WhisperAudioTranscriber : IAudioTranscriber, IDisposable` (`src/Scout.Transcription.Whisper/WhisperAudioTranscriber.cs:17`), with `TranscribeAsync` for a voice message and `TranscribeSegmentsAsync(wav, maxDuration, ct)` (`src/Scout.Transcription.Whisper/WhisperAudioTranscriber.cs:74`) for a recording.
  - Internal `WhisperLiveTranscriber : ILiveTranscriber` (`src/Scout.Transcription.Whisper/WhisperLiveTranscriber.cs:14`).
  - Internal `LiveUtteranceSegmenter` (`src/Scout.Transcription.Whisper/LiveUtteranceSegmenter.cs:12`).
  - Internal `OggOpusDecoder` (`src/Scout.Transcription.Whisper/OggOpusDecoder.cs:14`) and `WavDecoder` (`src/Scout.Transcription.Whisper/WavDecoder.cs:10`).
- **Configuration keys:** `Scout:Transcription:ModelPath`, a path to a ggml model file, not a credential. The desktop sets it to `ggml-large-v3-turbo.bin`, else `ggml-large-v3.bin`, under `userData/whisper-models`.
- **Packages:** `Whisper.net` 1.9.1 and `Whisper.net.Runtime` 1.9.1 (native whisper.cpp for linux, macOS and Windows), `Concentus` 2.2.2 and `Concentus.OggFile` 1.0.7 (managed Opus and Ogg).
- **External boundaries:** the model file on disk and the Whisper native library; no network.

## How it works

`TranscribeAsync` accepts `audio/wav` and `audio/x-wav` (`src/Scout.Transcription.Whisper/WhisperAudioTranscriber.cs:254`), `audio/ogg` and `audio/opus`, and throws `NotSupportedException` for anything else before the model loads (`src/Scout.Transcription.Whisper/WhisperAudioTranscriber.cs:61`). `WavDecoder.DecodeMono16k` walks the RIFF chunks to the `fmt ` and `data` chunks, skipping any others, and accepts only 16 kHz mono 16-bit PCM or 32-bit float, the extensible format included (`src/Scout.Transcription.Whisper/WavDecoder.cs:23`, `:70`); the samples go to Whisper as they are, with no Concentus and no resampling. It reads at most 5 minutes of samples (4,800,000, `:97`), straight from the stream into the float array through a pooled 64 KB buffer, with no copy of the data chunk's bytes, and a data chunk cut short gives the samples up to its last whole one (`:108`, `tests/Scout.Transcription.Whisper.Tests/WhisperWavTranscriptionTests.cs:159`); it throws `InvalidDataException` for any other format, a file that is not WAV or one with no samples, which the conductor turns into its "could not transcribe" reply. `OggOpusDecoder.DecodeMono16k` decodes at Opus's native 48 kHz and averages every three samples down to 16 kHz (`src/Scout.Transcription.Whisper/OggOpusDecoder.cs:29`): the managed decoder returns silence when it decodes a SILK (voice-mode) stream straight to 16 kHz, which the committed fixture reproduces. Decoding stops after 5 minutes of audio (`:27`); a stream with no decodable audio throws `InvalidDataException` (`:45`). The model loads on the first call, not when the service is resolved (`src/Scout.Transcription.Whisper/WhisperAudioTranscriber.cs:119`); the processor detects the language unless the decode names it and runs on half of the processors (at least one) so a local Ollama is not starved (`:22`), and `TranscribeAsync` joins the segments' text with nothing between them and trims it (`:67`, `:70`); a live decode joins and trims them the same way and hands on the language of the first segment (`:124`, `:127`). A file that is not a ggml model throws `WhisperModelLoadException` when the processor is built, which the conductor turns into its "could not transcribe" reply. One transcription runs at a time behind a semaphore (`:137`). After each decode, voice or live, a `TimeProvider` timer is re-armed for 10 minutes (`:172`); when it fires and no decode holds the gate, the model is disposed (`:186`) and the next voice message or live window loads it again. Loaded, large-v3-turbo holds about 1.6 GB and large-v3 about 3.1 GB. Model loading and transcription sit behind an internal `LoadedModel` seam, which receives a vendor-free `Decoding` record and yields the segments the model hears, each with its language (`src/Scout.Transcription.Whisper/WhisperAudioTranscriber.cs:266`), so the gate, the idle unload and the decode settings are tested without a real model.

`TranscribeSegmentsAsync` reads a 16 kHz mono WAV through the same `WavDecoder`, up to the `maxDuration` the caller gives instead of the voice messages' 5 minutes (`src/Scout.Transcription.Whisper/WhisperAudioTranscriber.cs:78`), so a WAV that is not one fails with `InvalidDataException` before the model loads, and yields each segment Whisper hears as `TranscribedSegment(Start, End, Text)`, Whisper.net's own start, end and text (`:248`), no Whisper.net type leaving the project. The gate is taken when the enumeration starts and released, with the idle timer re-armed, only when it ends or is disposed (`:137`, `:169`), so the recording holds the model for its whole transcription ([[pages/services/operator]]). A voice message waits at most 30 seconds for the gate, on the transcriber's `TimeProvider`, and is then refused with `TranscriptionBusyException`, which the conductor answers with "Scout is transcribing a recording right now; try the voice message again in a few minutes."; a recording waits without a bound, and the refused message never takes the gate (`src/Scout.Transcription.Whisper/WhisperAudioTranscriber.cs:21`, `:137`, `tests/Scout.Transcription.Whisper.Tests/WhisperAudioTranscriberLifetimeTests.cs:61`, [[pages/decisions/0024-voice-messages-transcribed-locally]]). The processor is released with `DisposeAsync`, which waits for the native run to stop, since its synchronous `Dispose` throws while it processes (`:163`); the operator cancels the run before it releases the segments, however its stage ends. The samples are held in memory for the whole run: 60 minutes are about 230 MB of floats beside the loaded model, read through the 64 KB buffer rather than a 115 MB copy of the WAV's bytes first.

**Live transcription** ([[pages/decisions/0034-live-transcription-on-the-local-adapter]]). `LiveUtteranceSegmenter` turns PCM16 little-endian bytes into float samples (divided by 32,768) and keeps an odd byte and a partial frame across chunks. It checks each 20 ms frame (320 samples) against an RMS of 0.015:
- An utterance opens on the first speech frame and ends after 50 silent frames (1 s), without that silence.
- An utterance with fewer than 8 speech frames (160 ms) yields nothing.
- Windows are cumulative, each the utterance from its start: the first at 12,000 samples (0.75 s), then one every 9,600 more (0.6 s). A window due during a pause waits for speech.
- Each window and each end reaches the session through a callback as it happens, in order (`src/Scout.Transcription.Whisper/LiveUtteranceSegmenter.cs:199`). A window's samples are copied only when the session asks, so a dropped window copies nothing.
- An utterance that reaches 320,000 samples (20 s) ends there with those 20 s and a new one starts at the next frame, so no words are dropped; the ring of the same size remains the hard bound.

`WhisperLiveTranscriber` decodes through the voice-message transcriber's model, gate and idle timer (`src/Scout.Transcription.Whisper/WhisperAudioTranscriber.cs:90`, `:101`):
- A window is decoded greedily, with no previous-text context and as one segment. Its audio context covers the window and 2 s more, window samples / 320 + 100 from 384 to 1000 (`src/Scout.Transcription.Whisper/WhisperAudioTranscriber.cs:290`). It starts only when the gate is free right now and no final waits, and is dropped otherwise, before its samples are copied.
- An ended utterance is decoded again with a beam of 5 and full context. It waits for the gate, in order, and is never dropped.
- A final detects the language. A partial detects it until the session's first final reports one, then is decoded in that language (`src/Scout.Transcription.Whisper/WhisperLiveTranscriber.cs:85`). Text is written before the gate is released, so a partial cannot land after its final, and an empty partial is not written.
- `StopAsync` flushes the open utterance as one final and waits for every decode. A decode failure, such as a file that is not a model, is rethrown on the next `FeedAsync` or by `StopAsync`.

Each window re-decodes the utterance from its start, so an utterance costs work in the square of its length. The 20 s cut bounds that: no window decodes more than 20 s. For live use base or small is the recommended model and large-v3-turbo stays the voice-message model, because a window must decode in under the 0.6 s hop to keep partials flowing. The desktop's model choice is the desktop lane's.

**Measured** on 2026-11-04, `ggml-base.bin`, a 5.23 s English sentence fed in 8,192-byte chunks every 256 ms, on a Ryzen 9 3900 (24 threads) under a load average of 9 to 20 from other work. Times run from the first chunk, over eight live runs before and five after, leaving out one run before that the load stalled (first partial 6.1 s, final 16.2 s); the decode times are medians of seven.

| | Before (audio context 1000) | After (per-window audio context, language, no copy on drop) |
|---|---|---|
| Partials per sentence | 3 (2 in one run) | 3 to 4 |
| First partial | 2.3 to 3.0 s | 2.0 to 2.3 s |
| Final | 7.4 to 8.9 s | 6.9 to 7.5 s |
| One partial decode, 0.75 to 4.35 s window | 1.22 to 1.27 s | 0.94 to 1.00 s |
| Final decode, 5.23 s (same settings both sides, so the gap is load) | 2.24 s | 1.65 s |

The first partial's text got rougher at the 384 context ("the quick brown." became "a quick brown for the white."); later partials and the final are unchanged. Building and disposing a Whisper.net processor takes about 8 µs, because Whisper.net 1.9.1 creates and frees the whisper state inside every `Process` call, so a processor is built per decode. Threads for a partial, 1.95 s and 4.35 s windows, interleaved over eight rounds (median): 4 threads 1.80 s, 6 1.33 s, 8 1.13 s, 12 1.04 s, 16 1.05 s. Half of the processors (12 here) stays the setting.

Publishing: Whisper.net loads its native libraries from `runtimes/<rid>/` beside the executable, which a single-file publish hides inside the bundle. A target in `src/Scout.Host.Console/Scout.Host.Console.csproj:40` keeps the target platform's Whisper natives as loose files next to the worker binary and drops the other platforms' copies; a single-file `linux-x64` publish then holds only `runtimes/linux-x64/` (about 4 MB), and the desktop packages the whole worker folder.

## Invariants

- [[pages/invariants/pluggable-boundaries]]: the core sees `IAudioTranscriber` and `ILiveTranscriber` only; no Whisper.net or Concentus type leaves this project.
- [[pages/invariants/no-executive-writes]]: reads a model file and a stream, writes nothing; live audio stays in memory.
- [[pages/invariants/local-first-default]]: transcription never calls a cloud service.

## Hot spots

- `src/Scout.Transcription.Whisper/OggOpusDecoder.cs:29`: the 48 kHz decode rate is load-bearing; see above.
- `src/Scout.Transcription.Whisper/WhisperAudioTranscriber.cs:29`: one transcription at a time (ponytail); a pool sized to memory if voice traffic queues. A recording of the owner's own video holds that gate for its whole transcription, minutes for a long one; a voice message waits at most 30 seconds behind it and gets the busy sentence. Transcribing in 5-minute windows, releasing the gate between them, is the upgrade path in [[pages/roadmap/followups]].

## Testing footprint

`tests/Scout.Transcription.Whisper.Tests` (43 test attributes, 50 test cases, 7 files. `LiveUtteranceSegmenterTests.cs` covers the cumulative window cadence, the 160 ms minimum, the 1 s end, the RMS threshold on both sides, the 20 s cut, chunks split mid-frame and mid-sample, and flush. `WhisperLiveTranscriberTests.cs`, through the model seam, covers greedy partials, the per-window audio context at 2.4 s, 8 s and 20 s, the beam-5 final, a window dropped during a decode and while a final is queued, a dropped window copying no samples, partials after the first final in its language, finals never dropped and in order, stop flushing one final, one model load shared with a voice message, the idle unload after a live session, the real `WhisperModelLoadException` failing a session over a file that is not a model, and a window's decode failure rethrown on the next feed. `WhisperServiceCollectionTests.cs` covers live registration over the voice-message instance, and nothing without a model file; `WhisperWavTranscriptionTests.cs` builds WAV files in memory and checks, through the model seam, that 16-bit PCM and float samples reach the model, that other chunks are skipped, that 6 minutes are cut to exactly 5, and that 44.1 kHz, stereo and non-WAV bytes are invalid data before the model loads, and for `TranscribeSegmentsAsync` that each segment comes back with its start and end, that the caller's limit, not 5 minutes, cuts the recording, that a 60-second WAV reaches the model sample for sample and one cut short up to its last whole sample, and that a file that is not WAV fails before the model loads) with a committed fixture, `Fixtures/tone-440hz-1500ms.ogg` (4.8 KB, a 1.5 s 440 Hz tone encoded in Opus voice mode, generated with Concentus; no speech): the fixture decodes to about 24,000 non-silent samples, decoding stops at the maximum duration, non-Ogg bytes are invalid data, a non-Ogg content type is refused before the model loads, a file that is not a model fails with `WhisperModelLoadException` (this loads the native runtime), the registration gate, a second voice message that does not enter the model until the first releases it, a voice message refused as busy after 30 seconds behind a recording that leaves the gate free, and the idle unload and reload with a fake clock (`WhisperAudioTranscriberLifetimeTests.cs`). Speech recognition is not asserted by a test; a manual run with `ggml-tiny.bin` on linux-x64 (AVX2) on 2026-10-28 ran inference in about 1.5 s and returned an empty transcript for the tone; the same run from a single-file `linux-x64` publish of a probe with the natives kept beside the binary also worked, while without that the loader reported "Native Library not found in default paths". A long recording is not timed yet: on 2026-11-04 the machine of the phase 8b run had no Whisper model file (the desktop's `whisper-models` folder was empty and no `ggml-*.bin` was under the home folder), and the run downloads nothing, so the operator's limit for a transcription, 10 minutes plus six times the recording's length (`src/Scout.Operator/TranscriptionStage.cs:46`), is not checked against a measured speed.

## Related

Direct project references: [[pages/services/conversation]]. Referenced by [[pages/services/host-console]]; its WAV input comes from [[pages/services/adapter-local]]. Map: [[pages/architecture/project-map]].

Streaming transcription of live audio is planned in [[pages/roadmap/live-assistant]].
