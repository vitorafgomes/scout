---
title: "ADR 0024 - Voice messages are transcribed locally, after the allow-list"
version: "1.2"
date: 2026-10-28
updated: 2026-11-04
changes: "Notes: a voice message waits at most 30 seconds behind a recording and then gets the busy sentence. Notes: the owner's long recordings share the transcriber's single gate, with the operator's 60-minute limit (ADR 0033, phase 8b)"
page_type: adr
status: accepted
description: "A Discord voice message is downloaded and transcribed on the owner's machine by a local Whisper model, only after the sender passes the allow-list and within 25 MB and 5 minutes, and the transcript is answered like typed text."
supersedes: ""
superseded_by: ""
source:
  - chat
  - src/Scout.Conversation/ConversationConductor.cs
  - src/Scout.Conversation/Abstractions/IAudioTranscriber.cs
  - src/Scout.Transcription.Whisper/
  - src/Scout.Adapter.Discord/
  - ui/packages/main/src/worker/whisper-model.ts
reliability: high
---

# ADR 0024 - Voice messages are transcribed locally, after the allow-list

> The owner can record a Discord voice message instead of typing; Scout transcribes it on this machine with a local Whisper model, only for an allowed sender and within 25 MB and 5 minutes, answers the transcript, and quotes what it heard at the top of the reply.

## Status

- [ ] Proposed
- [x] Accepted
- [ ] Deprecated / superseded

Updated: 2026-10-28.

## Context

The owner asked to "use audio to communicate - me recording". Discord sends a recorded voice message as a message flagged `IS_VOICE_MESSAGE` (`MessageFlags.VoiceMessage` = 8192 in Discord.Net 3.20.2) with one Ogg Opus attachment and a duration. The desktop app already downloads Whisper `ggml-large-v3` and `ggml-large-v3-turbo` weights under `userData/whisper-models` ([[pages/services/desktop-ui]]). Live voice in a voice channel is a separate spike and is not covered here. Audio is personal data, and every model call so far goes through the provider the owner chose ([[pages/decisions/0012-llm-communication-through-semantic-kernel]]); sending recordings to a cloud speech API would add a second destination the owner did not pick.

## Decision

- **Local only.** `IAudioTranscriber` (`src/Scout.Conversation/Abstractions/IAudioTranscriber.cs:8`) is the core abstraction; its one implementation, `WhisperAudioTranscriber` in [[pages/services/transcription-whisper]], runs the ggml model through Whisper.net on the CPU, decodes Ogg Opus with the managed Concentus decoder (no ffmpeg) and detects the spoken language. The audio never leaves the machine except for the download from Discord's CDN. Whisper is not reached through Semantic Kernel: it is not a chat or embedding model and is never LLM-invocable, so ADR 0012 does not apply to it.
- **Registered only when a model exists.** The host calls `AddWhisperTranscription` (`src/Scout.Host.Console/HostComposition.cs:64`), which registers the transcriber only when `Scout:Transcription:ModelPath` names an existing file (`src/Scout.Transcription.Whisper/WhisperServiceCollectionExtensions.cs:29`). The desktop sets `SCOUT__TRANSCRIPTION__MODELPATH` to `ggml-large-v3-turbo.bin`, else `ggml-large-v3.bin`, under `userData/whisper-models`, and to nothing when neither is installed (`ui/packages/main/src/worker/whisper-model.ts:125`). The model loads on the first voice message, not at startup.
- **After the allow-list.** `ConversationConductor.HandleAsync` runs the [[pages/decisions/0021-allowed-users]] gate first (`src/Scout.Conversation/ConversationConductor.cs:132`); a refused sender's audio is never downloaded or transcribed. The adapter hands the core a download delegate on `AudioAttachment` (`src/Scout.Conversation/Abstractions/AudioAttachment.cs:13`), so nothing is fetched until the conductor asks and no Discord type reaches the core ([[pages/invariants/pluggable-boundaries]]).
- **Limits.** 25 MB and 5 minutes, checked on the size and duration Discord reports before the download (`src/Scout.Conversation/ConversationConductor.cs:288`, `:292`), then again on the bytes actually read (`:357`); the whole download, body included, must finish within 30 s or it gets the download-failed reply (`:301`); the decoder stops after 5 minutes of audio whatever the file claims (`src/Scout.Transcription.Whisper/OggOpusDecoder.cs:27`). One transcription runs at a time (`src/Scout.Transcription.Whisper/WhisperAudioTranscriber.cs:140`), Whisper uses half of the processors so a local Ollama keeps running (`:232`), and the model is unloaded after 10 minutes without a voice message and reloaded by the next one (`:175`, `:189`). The "Transcribed a voice message" log line carries the duration in ms, never the transcript.
- **Replies.** The transcript is routed as the message text, so memory and the model see what was said; the reply starts with `> (voice) ` and the first 200 characters of the transcript on one line. Fixed replies, each through the same sink: no transcriber, "Voice messages need a Whisper model. Download one in the Scout app under AI Sources, Local Models."; over the size or duration limit; the download failed; the transcription failed; no speech heard. Logs carry the channel id and the transcript length, never the transcript.
- **Classification.** Downloading the attachment is a read. The quote and the reply are descriptive writes in the originating channel or thread ([[pages/decisions/0009-descriptive-vs-executive-writes]]).
- **Same change, Discord chat.** A mention in a server text channel now opens a public thread on the user's message, named from the first 60 characters of the question, and the reply streams there (`Scout:Adapters:Discord:ReplyInThreads`, default true). Creating that thread is a descriptive write in the originating channel. The adapter opens it only for a sender `AccessPolicy.IsAllowed` admits (`src/Scout.Conversation/Access/AccessPolicy.cs:42`), the same decision the conductor's allow-list uses, so a refused sender gets no thread: the refusal is posted in the channel as a reply to their message and stays the only thing a refused message causes ([[pages/decisions/0021-allowed-users]], [[pages/services/adapter-discord]]).

## Consequences

- **Better:** the owner can talk to Scout from a phone without typing; recordings stay on the machine; a refused user costs no download and no CPU.
- **Worse:** large-v3 on CPU is slow (seconds to tens of seconds per message) and the loaded model is resident in memory, about 1.6 GB for large-v3-turbo and 3.1 GB for large-v3, until 10 idle minutes unload it; the first voice message after that pays the reload; the worker carries Whisper native libraries (about 4 MB per platform); a model downloaded while the worker runs is picked up only at the next worker start.
- **Neutral but noteworthy:** a voice message cannot carry an @-mention, so in a server channel or thread Scout answers a voice message only when it replies to one of Scout's messages; in a DM any voice message is answered. Speech recognition quality was not verified by an automated test: the tests decode a committed tone fixture and map a model-load failure, and a manual run with `ggml-tiny` confirmed native inference on linux-x64.

## Alternatives considered

- **A cloud speech API (OpenAI transcription, Gemini audio).** Sends recordings to a provider; for an owner on Ollama it would be a second provider they did not choose.
- **ffmpeg for decoding.** A native dependency to ship on three platforms; the managed decoder covers Ogg Opus, the only format Discord uses for voice messages.
- **Transcribe before the allow-list.** Spends CPU and downloads audio for senders Scout will refuse anyway.
- **Status quo.** Voice messages are dropped as empty messages.

## Enforcement

- `tests/Scout.Conversation.Tests/ConversationConductorVoiceTests.cs`: transcript routed and quoted, refused user's audio never downloaded, size, actual size and duration refusals, download and transcriber failures, no speech, and the exact no-model text.
- `tests/Scout.Transcription.Whisper.Tests/OggOpusDecoderTests.cs` and `WhisperAudioTranscriberTests.cs`: fixture decodes to 16 kHz, decode cap, invalid data, model-load failure, registration gate.
- `tests/Scout.Host.Console.Tests/HostVoiceCompositionTests.cs`: the composed host answers with the no-model text or hands the audio to the transcriber.
- `tests/Scout.Adapter.Discord.Tests/DiscordVoiceMessageTests.cs` and `DiscordChatAdapterThreadTests.cs`; `ui/packages/main/test/worker-whisper-model.test.ts` and the voice block in `ui/packages/main/test/worker-build-env.test.ts`.
- Invariants: [[pages/invariants/no-executive-writes]], [[pages/invariants/pluggable-boundaries]], [[pages/invariants/secrets-never-in-source]].

## Related

- Services affected: [[pages/services/conversation]], [[pages/services/adapter-discord]], [[pages/services/transcription-whisper]], [[pages/services/host-console]], [[pages/services/desktop-ui]]
- Related decisions: [[pages/decisions/0021-allowed-users]], [[pages/decisions/0009-descriptive-vs-executive-writes]], [[pages/decisions/0013-conversation-core-streaming-sink]]

## Notes

Out of scope: live voice in a Discord voice channel, Slack and Teams audio, and choosing a smaller model from the app.

Long recordings (2026-11-04, [[pages/decisions/0033-channel-operator-architecture]], phase 8b): the YouTube operator transcribes the owner's own video with the same transcriber, through `TranscribeSegmentsAsync` on a 16 kHz mono WAV, behind the same single gate (`src/Scout.Transcription.Whisper/WhisperAudioTranscriber.cs:140`), held for the whole recording. A voice message that arrives meanwhile waits at most 30 seconds for the gate (`src/Scout.Transcription.Whisper/WhisperAudioTranscriber.cs:21`, `tests/Scout.Transcription.Whisper.Tests/WhisperAudioTranscriberLifetimeTests.cs:61`); past that the transcriber throws `TranscriptionBusyException` (`src/Scout.Conversation/Abstractions/IAudioTranscriber.cs:27`) and the conductor answers "Scout is transcribing a recording right now; try the voice message again in a few minutes." (`src/Scout.Conversation/ConversationConductor.cs:59`, `:323`, `tests/Scout.Conversation.Tests/ConversationConductorVoiceTests.cs:269`), so a voice message is never held for the length of a recording; the recording itself waits for as long as it takes. The operator's limit is 60 minutes of recording (`src/Scout.Operator/TranscriptionStage.cs:19`); the 25 MB and 5-minute limits above stay for voice messages ([[pages/services/transcription-whisper]]).
