---
title: "Transcription.Whisper: How it works: Each window re-decodes the utterance from"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Transcription.Whisper service page."
page_type: service
status: active
description: "Read when a change touches: Each window re-decodes the utterance from; Measured; The first partial's text got rougher; Publishing: Whisper.net loads its native libraries."
source: ["src/Scout.Transcription.Whisper/", "tests/Scout.Transcription.Whisper.Tests/", "src/Scout.Host.Console/Scout.Host.Console.csproj"]
reliability: high
---

## How it works

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
