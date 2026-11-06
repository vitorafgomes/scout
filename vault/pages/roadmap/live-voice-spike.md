---
title: "Live voice conversation in Discord (spike)"
version: "1.0"
date: 2026-10-28
updated: 2026-10-28
changes: "Spike findings, verdict, phased plan and open owner decisions"
page_type: roadmap
status: active
description: "Research spike for Scout joining a Discord voice channel, listening only to allowed users, transcribing locally, answering through the conversation core and speaking the answer; verdict go, gated on a Discord.Net DAVE fix, with a sidecar as the fallback."
source:
  - chat
  - https://docs.discord.com/developers/topics/voice-connections
  - https://discord.com/blog/bringing-dave-to-all-discord-platforms
  - https://support.discord.com/hc/en-us/articles/38749827197591-A-V-E2EE-Enforcement-for-Non-Stage-Voice-Calls
  - https://github.com/discord-net/Discord.Net/releases
  - https://github.com/discord-net/Discord.Net/issues/3281
  - https://github.com/discord-net/Discord.Net/pull/3288
  - https://github.com/discord-net/Discord.Net/blob/dev/samples/Audio/Recorder.cs
  - https://github.com/discord-net/Discord.Net/blob/dev/docs/guides/voice/libdave.md
  - https://github.com/discord/libdave/releases
  - https://github.com/NetCordDev/NetCord
  - https://github.com/discordjs/discord.js/issues/11419
  - https://github.com/discordjs/discord.js/pull/11449
  - https://github.com/discord/discord-api-docs/issues/808
  - https://github.com/sandrohanea/whisper.net/releases
  - https://developers.openai.com/api/docs/guides/text-to-speech
reliability: medium
---

# Live voice conversation in Discord (spike)

> When this is done, an allowed user types a command, Scout joins their Discord voice channel, hears only the allowed users, transcribes them on the owner's machine, answers through the same conversation core as text, and speaks the answer back; nobody else is transcribed and nothing is stored.

Draft decision: [[pages/decisions/0026-live-voice-conversation]] (proposed).

## Verdict

**Go, in-process on Discord.Net, gated on the DAVE fix in Discord.Net PR #3288 landing in a release; go-with-sidecar (`@discordjs/voice` 0.19.2) only if that release does not come.** Phase 1 (listen-only, text reply) needs no TTS and no new provider.

Why not no-go: per-user receive with DAVE exists today in three libraries (Discord.Net 3.20.x, NetCord beta, `@discordjs/voice` 0.19.2). Why not plain go: the version Scout pins, Discord.Net 3.20.2, still carries the MLS user-id bug that breaks DAVE joins (verified at the tag, below), and Discord itself has never supported voice receive for bots.

## Findings

All sources accessed 2026-10-28; the date in brackets is the source's own.

### 1. DAVE (Discord audio/video E2EE)

- Discord voice is E2EE only since March 2026. The voice docs: Discord will "only support E2EE calls starting on March 1st, 2026"; a client signals no DAVE by sending `max_dave_protocol_version` 0 or omitting it, and the gateway picks "the lowest shared protocol version". [docs.discord.com voice connections](https://docs.discord.com/developers/topics/voice-connections) (living page).
- Discord's blog: "Starting March 1st 2026, clients and apps without DAVE support will no longer be able to participate in Discord calls." [Bringing DAVE to all platforms](https://discord.com/blog/bringing-dave-to-all-discord-platforms). Discord support says a client without DAVE is rejected with voice close code 4017; stage channels are exempt. [A/V E2EE enforcement](https://support.discord.com/hc/en-us/articles/38749827197591-A-V-E2EE-Enforcement-for-Non-Stage-Voice-Calls).
- **Bots must support DAVE to join any non-stage voice channel.** There is no opt-out for the bot; the only escape is a stage channel.
- Protocol: MLS group per call, voice gateway opcodes 21 to 31 (prepare/execute transition, external sender, key package, proposals, commit/welcome, invalid commit/welcome), some binary; transport encryption `aead_aes256_gcm_rtpsize` preferred, `aead_xchacha20_poly1305_rtpsize` mandatory. The docs point to Discord's open-source `libdave`. [voice connections](https://docs.discord.com/developers/topics/voice-connections).
- `libdave` is MIT, ships prebuilt `libdave-{Linux,macOS,Windows}-{X64,ARM64}-boringssl.zip`; latest `v1.2.2/cpp` [2026-10-23]. [discord/libdave releases](https://github.com/discord/libdave/releases).

### 2. Discord.Net (repo pins 3.20.2 in `Directory.Packages.props`)

- Releases: 3.19.0 [2026-03] "introduces libdave support" (#3241) and close code 4017; 3.19.1 [2026-03-12] fixes libdave for users joining/leaving; 3.20.0 [2026-06-06] strips RTP padding before DAVE decrypt (#3263); 3.20.2 [2026-10-08] adds an audio recording sample (#3283). 3.19.0-beta.1 lists "Fix voice receiving" (#3190). [releases](https://github.com/discord-net/Discord.Net/releases).
- **Receive per user: yes.** The recording sample subscribes to `IAudioClient.StreamCreated(ulong userId, AudioInStream stream)` and reads `RTPFrame`s per user, decoded to 48 kHz stereo PCM. [samples/Audio/Recorder.cs](https://github.com/discord-net/Discord.Net/blob/dev/samples/Audio/Recorder.cs) (merged 2026-10-04). Speaker identity comes from the library's SSRC-to-user map, not from audio content.
- **DAVE: yes, with native setup.** `Discord.Net.WebSocket` 3.20.2 depends on the managed `Discord.Net.Dave` bindings (checked in the nuspec); the native `libdave` must sit in the executing directory or on `PATH`, and `DiscordSocketConfig.EnableVoiceDaveEncryption` (default `null`, auto) opts in or out. [libdave guide](https://github.com/discord-net/Discord.Net/blob/dev/docs/guides/voice/libdave.md). The audio sample also downloads libopus and libsodium natives.
- **Blocking bug in 3.20.2.** Issue #3281 [opened 2026-08-01, open]: DAVE join fails with "MLS Failure: ValidateProposalMessage -> Unexpected user ID in add proposal", then "Malformed Frame" every second; with two or more users the bot loops and is disconnected with 4021. [issue #3281](https://github.com/discord-net/Discord.Net/issues/3281). Fix PR #3288 [opened 2026-10-02, approved 2026-10-04, **not merged** on 2026-10-28]: `ToCString` lacks a null terminator, and `DaveSessionManager` omits the bot's own id from recognised ids. [PR #3288](https://github.com/discord-net/Discord.Net/pull/3288). Verified at tag `3.20.2`: `tools/Discord.Net.Dave/Utils.cs` `ToCString` writes no terminator, and `src/Discord.Net.WebSocket/Audio/DaveSessionManager.cs` passes `_decryptors.Keys` to `ProcessWelcome`.
- Other open voice issues worth knowing: #3208 [2025-12-17] a user speaking as they join is not heard.

### 3. Alternatives

| Option | Per-user receive | DAVE | Status | Notes |
|---|---|---|---|---|
| Discord.Net 3.20.2 (in repo) | yes (`StreamCreated`) | yes, via libdave | stable, but #3281 breaks DAVE joins until #3288 ships | no new gateway session; MIT |
| NetCord 1.0.0-beta.28 [2026-10-01] | yes (`VoiceReceive` with `Ssrc`, `Cache.SsrcUsers`) | yes, libdave P/Invoke since "Implement end-to-end encryption (#168)" [2026-02-09] | beta | [NetCord](https://github.com/NetCordDev/NetCord), [voice guide](https://netcord.dev/guides/voice/); MIT; a second library and a second gateway session on the same token |
| DSharpPlus.Voice 5.0.0-nightly [2026-09] | not confirmed | yes (`DSharpPlus.Voice/E2EE/MlsSession.cs`) | nightly, about six weeks old | VoiceNext is deprecated |
| `@discordjs/voice` 0.19.2 [2026-03-17] sidecar | yes (`VoiceReceiver.subscribe(userId)`, `speaking`, `ssrcMap`) | yes, needs `@snazzah/davey` 0.1.x | receive broke on DAVE in 0.19.0 (#11419); fixed by #11449 [merged 2026-03-13] | Apache-2.0; adds Node to the worker; [issue #11419](https://github.com/discordjs/discord.js/issues/11419), [PR #11449](https://github.com/discordjs/discord.js/pull/11449), [VoiceReceiver](https://discord.js.org/docs/packages/voice/main/VoiceReceiver:Class) |
| Python (py-cord) | reported to receive on DAVE | reported | third-party report only | not verified here; adds Python |
| Lavalink | no | n/a | playback only | not a fit for receive |

A sidecar or NetCord opens a second gateway session on the same bot token: it must identify with no message intents or both sessions receive every message. `@discordjs/voice` can in principle take an `adapterCreator` fed by another gateway, which would let the .NET session forward voice state; this is unverified.

**Receive is unsupported by Discord.** A Discord staff member on voice receive: "Unless it's documented, it's not our intent that it is to be used by bots ... we are currently not offering support for it." [discord-api-docs #808](https://github.com/discord/discord-api-docs/issues/808) [2019-01]. Every library implements it anyway; any of them can break without notice.

### 4. Local speech to text

- Whisper.net 1.9.1 [2026-06-01] adds VAD (`WhisperVadFactory`, `WhisperVadProcessor`, Silero `ggml-silero-v6.2.0.bin` through `WhisperGgmlDownloader`) and a Parakeet TDT 0.6B v3 engine; MIT. [releases](https://github.com/sandrohanea/whisper.net/releases). Whisper.net has no streaming decoder: the live pattern is VAD-segmented utterances, each transcribed whole.
- whisper.cpp (MIT, [ggml-org/whisper.cpp](https://github.com/ggml-org/whisper.cpp)) has a `stream` example with sliding windows; faster-whisper (MIT, CTranslate2, [SYSTRAN/faster-whisper](https://github.com/SYSTRAN/faster-whisper)) is faster on CPU but is Python, so sidecar only.
- Latency: not measured in this spike (no prototype). Expected order of magnitude for a 3 to 5 s utterance: under a second for base/small or Parakeet on a modern CPU, several seconds for Large v3 on CPU. To measure in phase 1.
- The desktop today downloads only Whisper Large v3 and v3-Turbo GGML ([[pages/decisions/0008-local-models-real-downloads]]); live use likely needs a smaller model or Parakeet, which is a desktop change.

### 5. Text to speech

- **Kokoro-82M**: weights Apache-2.0 ([hexgrad/Kokoro-82M](https://huggingface.co/hexgrad/Kokoro-82M)); KokoroSharp 0.8.4 (MIT, ONNX Runtime, "text-segment streaming", depends on MisakiSharp 2.2.0, Apache-2.0) ([KokoroSharp](https://github.com/Lyrcaxis/KokoroSharp)). In-process .NET, a new model download the owner must opt into. Phonemizer coverage for languages other than English not checked.
- **Piper**: the maintained repo is now `OHF-Voice/piper1-gpl`, **GPL-3.0**; the MIT `rhasspy/piper` is archived. Bundling it in an MIT app is a licensing question; per-voice licences vary.
- **sherpa-onnx** (Apache-2.0, NuGet `org.k2fsa.sherpa.onnx` 2.0.1; its README lists Silero VAD, Whisper and other STT models, and C# bindings) may cover VAD, STT and TTS with one native dependency. Its TTS model support and phonemizer licence (espeak-ng is GPL-3.0) are not verified here.
- **OpenAI cloud TTS is ending.** The OpenAI guide: "The `tts-1`, `tts-1-hd`, and `gpt-4o-mini-tts` models are deprecated" and "will shut down on January 6, 2027"; the replacement is `gpt-realtime-2.1-mini` through the Realtime API, which "isn't a supported model for `/v1/audio/speech`". PCM output is 24 kHz 16-bit mono. Usage policy requires "a clear disclosure to end users" that the voice is AI-generated. [OpenAI text to speech](https://developers.openai.com/api/docs/guides/text-to-speech).
  - Consequence: cloud TTS would work only for an owner on an OpenAI API key, only until 2027-01-06; the Realtime replacement bypasses Semantic Kernel ([[pages/decisions/0012-llm-communication-through-semantic-kernel]]); the ChatGPT plan path excludes audio ([[pages/roadmap/chatgpt-plan-provider]]). Under "use only the provider the owner connected", an owner on Ollama, Gemini or the ChatGPT plan has no cloud TTS at all, so local TTS is the only path that works for every owner.

### 6. Turn-taking, speakers, allow-list, privacy

- **Who is speaking**: the SSRC-to-user mapping is not described in Discord's voice docs; each library builds it from undocumented voice-gateway events and exposes one stream per user (Discord.Net `StreamCreated(userId, ...)`, NetCord `Cache.SsrcUsers`, discord.js `ssrcMap`), so no diarisation is needed.
- **Turn-taking**: per-user VAD; an utterance ends after about 700 to 1000 ms of silence (tunable); one in-flight answer per channel; a new utterance from an allowed user while Scout speaks stops playback (barge-in) and cancels the in-flight answer through the existing cancellation token.
- **Allow-list per speaker, before anything else.** [[pages/decisions/0021-allowed-users]] gates in `ConversationConductor` (`src/Scout.Conversation/ConversationConductor.cs:89`), which for voice is after transcription. Voice needs the same rule earlier: frames from a non-allowed id are dropped as they arrive, never buffered, never passed to VAD or STT. Honest limit: the library still decrypts every speaker's packets in memory to read them; Scout can promise "not buffered, not transcribed, not stored", not "never decrypted".
- **Open-by-default is unsafe for voice.** With an empty list and `RequireAllowList` false the text path answers everyone; in voice that means transcribing bystanders. Voice must require a non-empty list, whatever `RequireAllowList` says.
- **Nothing stored.** PCM lives in a per-utterance buffer, released after transcription; no WAV files, no transcripts in logs (log the channel id and durations only), transcripts reach conversation memory exactly as typed text does today.
- **Discord Developer Policy** returned 403 to the fetch; what it says about voice data is unverified.

## Architecture options

| Option | Shape | Complexity | Notes |
|---|---|---|---|
| **A. In-process Discord.Net** (recommended) | voice code in `Scout.Adapter.Discord`, STT/TTS behind small interfaces | medium | same gateway session; ships libdave, libopus, libsodium natives in the AppImage; blocked until #3288 is released (or a local patched build) |
| B. NetCord voice-only client in-process | second .NET library just for voice | medium-high | second gateway session; beta library; two Discord SDKs in one adapter |
| C. Node sidecar with `@discordjs/voice` | worker spawns a Node process; PCM or text over stdio/UDS | high | proven DAVE receive today; a new runtime, a new process to supervise, a new IPC contract and a failing-call test for it (Rule I) |
| D. Cloud realtime (OpenAI Realtime) | speech-to-speech | n/a | rejected: bypasses SK, needs an API key, sends audio off the machine |

## Phased plan

### Phase 0 - Unblock (no Scout code)
- Watch Discord.Net PR #3288; if not released by the time phase 1 starts, build Discord.Net `dev` with the patch locally in the throwaway spike only, never in the repo.
- Owner reads the Discord Developer Policy on voice data.

### Phase 1 - Listen-only, text reply (smallest useful)
- An allowed user sends `@Scout join` in a text channel while in a voice channel; Scout joins that voice channel (intent `GuildVoiceStates` added next to the others at `src/Scout.Adapter.Discord/DiscordChatAdapter.cs:58`).
- Per allowed speaker: Opus/PCM frames, downmix to 16 kHz mono, Whisper.net VAD, transcribe the utterance, build a normal `ChatMessage` with the speaker's snowflake as `UserId`, hand it to the existing handler; the reply goes to the text channel where `join` was typed, through the existing `DiscordResponseSink`. The conductor, router and sink stay unchanged.
- The allow-list check is extracted from `ConversationConductor.RefusalFor` into `Scout.Conversation.Access` so the voice receiver and the conductor call the same rule (second caller, Rule C).
- `@Scout leave`, and auto-leave when no allowed user remains in the channel. Never auto-join.
- Exit: measured latency per model on the owner's machine; DAVE join stable with 2+ users.

### Phase 2 - Spoken answers
- Local TTS (Kokoro via KokoroSharp or sherpa-onnx), 24 kHz to 48 kHz stereo Opus, spoken in the voice channel; the text reply stays in the text channel as the record.
- A short spoken disclosure on join ("Scout, an AI assistant, is listening to allowed users").
- Barge-in.

### Phase 3 - Desktop
- Voice toggle, VAD sensitivity, STT model choice (adds a small model or Parakeet to the Whisper downloads), TTS voice download.

## Test strategy

- **Unit, no Discord:** the per-speaker gate (non-allowed id: zero frames reach the buffer; empty list: voice refuses to join even with `RequireAllowList` false); VAD segmentation on generated PCM (sine plus silence, no real recordings); utterance to `ChatMessage` projection; join/leave command parsing; auto-leave rule.
- **Seams faked with NSubstitute:** a small voice-receive interface over `IAudioClient` so tests push frames per user id without a gateway; the STT and TTS interfaces.
- **Failing-call tests (Rule I):** voice connection drops mid-utterance (buffer released, no message sent); STT throws (the user gets a text error in the text channel, nothing retried in a loop); TTS throws (text reply still posted); sidecar exit if option C is taken.
- **Privacy tests:** no file is written during a session (temp directory asserted empty); logs carry no transcript text or user id (same style as `tests/Scout.Conversation.Tests/ConversationConductorAccessTests.cs:122`).
- **Manual only:** a live DAVE join with 2+ humans; not automatable in CI and never with the owner's real server in recorded fixtures.

## Risks

- **Unsupported surface.** Discord does not support bot voice receive; a voice gateway change can break it overnight, as DAVE did for discord.js in March 2026.
- **Dependency on an unreleased fix.** Phase 1 cannot ship on 3.20.2.
- **Native libraries.** libdave, libopus and libsodium per OS/arch in the AppImage and other packages; a missing `libdave` falls back to no DAVE and a 4017 rejection.
- **CPU and latency.** Large v3 on CPU is likely too slow for conversation; a small model trades accuracy.
- **Privacy and consent.** Other people in the channel hear that a bot is present but may not know it listens; transcription of allowed users only, plus a join disclosure, is the mitigation. Local law on recording consent is the owner's call.
- **Access semantics.** Ids are not platform-qualified ([[pages/decisions/0021-allowed-users]]); a Discord voice speaker is matched by snowflake only.
- **Cloud TTS sunset** on 2027-01-06 makes OpenAI TTS a dead end.

## Open owner decisions

1. **Wait for Discord.Net #3288 (A) or build the Node sidecar now (C)?** Recommendation: wait; start phase 1 work against a locally patched build in the scratchpad.
2. **Voice requires a non-empty allow-list** even when text is open (recommended yes).
3. **Phase 1 scope**: listen-only with text replies first (recommended), or straight to spoken answers.
4. **Join trigger**: text command only (recommended), slash command, or both.
5. **Local TTS engine and download**: Kokoro (Apache-2.0) via KokoroSharp or sherpa-onnx; Piper rejected for GPL-3.0 unless the owner accepts it as a separately downloaded model.
6. **STT model for live use**: add a small Whisper or Parakeet download to the desktop.
7. **Cloud TTS for OpenAI API-key owners**: skip (recommended, sunset 2027-01-06) or offer until then.
8. **Consent**: spoken disclosure on join, and whether to post a text notice in the voice channel's chat.

## Prototype

Skipped. A meaningful check needs a live bot token, a guild and two humans in a voice channel; the spike does not use the owner's accounts or data. Package metadata and source at tag `3.20.2` were inspected instead (Discord.Net.Dave nuspec, `Utils.cs`, `DaveSessionManager.cs`, `DiscordSocketConfig.cs`).

## Services affected

[[pages/services/adapter-discord]], [[pages/services/conversation]], [[pages/services/desktop-ui]], [[pages/services/host-console]].
