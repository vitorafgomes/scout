---
title: "ADR 0026 - Live voice conversation in Discord through Discord.Net, local speech, allowed speakers only"
version: "1.0"
date: 2026-10-28
changes: "Initial ADR draft"
page_type: adr
status: proposed
description: "Scout joins a Discord voice channel only on an allowed user's command, uses Discord.Net with libdave for DAVE, drops audio from non-allowed speakers before any buffering, transcribes and speaks locally, and hands each utterance to the conversation core as an ordinary chat message."
supersedes: ""
superseded_by: ""
source:
  - chat
  - pages/roadmap/live-voice-spike.md
  - https://docs.discord.com/developers/topics/voice-connections
  - https://github.com/discord-net/Discord.Net/pull/3288
  - https://developers.openai.com/api/docs/guides/text-to-speech
reliability: medium
---

# ADR 0026 - Live voice conversation in Discord through Discord.Net, local speech, allowed speakers only

> Scout talks in Discord voice through the Discord.Net session it already has, hears only allowed users, does speech on the owner's machine, and treats each utterance as a normal chat message.

## Status

- [x] Proposed
- [ ] Accepted
- [ ] Deprecated / superseded

Updated: 2026-10-28.

## Context

The owner wants Scout to hold a live spoken conversation in a Discord voice channel. Since March 2026 Discord accepts only DAVE (MLS-based end-to-end encrypted) voice clients in non-stage channels; others are closed with 4017. Discord has never supported bots receiving voice, though every major library implements it. Discord.Net, which Scout already uses, supports per-user receive (`IAudioClient.StreamCreated`) and DAVE through the native `libdave`, but the pinned 3.20.2 has a DAVE join bug (issue #3281) whose approved fix (PR #3288) is not released. Text access is gated in the conversation core ([[pages/decisions/0021-allowed-users]]), which for voice runs after transcription, too late for privacy. OpenAI's speech endpoint models shut down on 2027-01-06 and their replacement bypasses Semantic Kernel ([[pages/decisions/0012-llm-communication-through-semantic-kernel]]). Evidence and sources: [[pages/roadmap/live-voice-spike]].

## Decision

- **Transport.** Voice lives in `Scout.Adapter.Discord` on the existing Discord.Net client, with `GuildVoiceStates` added to the intents and `libdave`, libopus and libsodium shipped as natives. No second gateway session, no sidecar. Work starts once a Discord.Net release contains the #3288 fix.
- **Presence.** Scout joins a voice channel only when an allowed user, present in that channel, asks in text; it leaves on command or when no allowed user remains. It never joins on its own.
- **Speaker gate before audio is kept.** The allowed-users rule moves from `ConversationConductor` into `Scout.Conversation.Access` so the voice receiver and the conductor call the same rule. Frames from a non-allowed speaker are discarded on arrival: never buffered, never run through VAD or STT, never stored. Voice requires a non-empty allow-list regardless of `RequireAllowList`.
- **Speech stays local.** STT is Whisper.net with its Silero VAD, one utterance at a time; TTS is a local model (Kokoro-82M, Apache-2.0) in-process. Neither is a chat or embedding model, so neither goes through Semantic Kernel; neither sends audio off the machine, which keeps [[pages/invariants/local-first-default]] and "only the provider the owner connected" intact.
- **One conversation path.** Each utterance becomes a `ChatMessage` with the speaker's snowflake as `UserId` and goes through the existing handler, router and LLM; the text answer is posted in the text channel where the join was asked, and in phase 2 also spoken.
- **Nothing persisted.** PCM is held per utterance in memory and released after transcription; logs carry channel ids and durations, never transcripts or user ids.
- **Classification ([[pages/decisions/0009-descriptive-vs-executive-writes]]).** Joining a voice channel is the bot's own presence, like being online; speaking an answer is a descriptive write in the originating context. No voice feature may mutate anything ([[pages/invariants/no-executive-writes]]).

## Consequences

- **Better:** one Discord session and one library; no new runtime; the conversation core is unchanged; non-allowed people in the channel are not transcribed; works for every owner, whatever LLM provider they connected.
- **Worse:** blocked on an upstream release; three native libraries per OS/arch in the desktop packages; relies on a surface Discord does not support; local STT/TTS costs CPU and adds model downloads; the library still decrypts all speakers in memory before Scout drops them.
- **Neutral but noteworthy:** the allow-list rule gains a second caller and moves to `Scout.Conversation.Access`; Discord.Net bugs in voice (for example #3208, a user speaking as they join is not heard) become Scout's.

## Alternatives considered

- **Node sidecar with `@discordjs/voice` 0.19.2 and `@snazzah/davey`.** DAVE receive works today, but adds Node, a supervised process, an IPC contract and a second gateway session. Kept as the fallback if the Discord.Net fix stalls.
- **NetCord (beta) for voice only.** Has DAVE and per-SSRC receive, but a second Discord SDK and gateway session in one adapter.
- **OpenAI speech or Realtime API.** Endpoint models shut down 2027-01-06; Realtime bypasses Semantic Kernel; sends audio to a cloud; unavailable to owners on Ollama, Gemini or the ChatGPT plan.
- **Gate only in the conductor.** Every bystander would be transcribed before refusal.
- **Status quo (text only).** Rejected only if the owner still wants voice after reading the risks.

## Enforcement

- Tests to write with the implementation: a non-allowed speaker's frames never reach the utterance buffer or STT; an empty allow-list refuses to join with `RequireAllowList` false; no file is written during a voice session; voice logs carry no transcript or user id; failing-call tests for voice disconnect, STT failure and TTS failure (Rule I, [[pages/conventions/testing]]).
- Invariants: [[pages/invariants/no-executive-writes]], [[pages/invariants/pluggable-boundaries]] (no Discord.Net or speech-engine type crosses into `Scout.Conversation`), [[pages/invariants/local-first-default]], [[pages/invariants/secrets-never-in-source]].

## Related

- Services affected: [[pages/services/adapter-discord]], [[pages/services/conversation]], [[pages/services/desktop-ui]]
- Related decisions: [[pages/decisions/0008-local-models-real-downloads]], [[pages/decisions/0009-descriptive-vs-executive-writes]], [[pages/decisions/0012-llm-communication-through-semantic-kernel]], [[pages/decisions/0021-allowed-users]]
- Spike: [[pages/roadmap/live-voice-spike]]

## Notes

The TTS engine (Kokoro), the non-empty allow-list rule for voice and the text-command join trigger are recommendations pending owner decisions 5, 2 and 4 in [[pages/roadmap/live-voice-spike]]; the ADR is accepted only after those are answered. The Discord Developer Policy text on voice data could not be fetched (403) and must be read before acceptance.
