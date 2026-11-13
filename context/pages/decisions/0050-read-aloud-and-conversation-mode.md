---
title: "ADR 0050 - Read aloud and conversation mode"
version: "1.3"
date: 2026-11-07
changes: "Amended 2026-11-08 (the microphone and the speaker): the owner chooses both in Settings, Audio, kept in renderer storage like the read-aloud switch, an unplugged one falling back to the default with one notice; a Test meter shows the room and the worker's speech threshold; a refused, missing or."
page_type: adr
status: accepted
description: "The desktop chat and quick ask can read Scout's answers aloud, sentence by sentence as they stream, with the voice of the connected AI Source when it has one or Scout's local voice otherwise; and a conversation mode listens, sends on a pause, reads the answer aloud and listens again until the owner stops it."
supersedes: ""
superseded_by: ""
source:
  - chat
  - pages/services/operator.md
  - pages/services/transcription-whisper.md
reliability: high
---

# ADR 0050 - Read aloud and conversation mode

> Dictation stays as it is; a second icon talks with Scout: it listens, answers aloud and listens again.

## Status

- [x] Proposed
- [x] Accepted

Built: 2026-11-07. The worker answers a `speak` line with the audio of one sentence: the connected AI Source's text-to-audio as MP3 when it has speech, else the bundled Piper voice as a WAV rendered in `<userData>/speech` and deleted once read back, else the no-voice reason ([[pages/services/adapter-local]], [[pages/services/operator]]). Main carries it on `scout:local.speak`; the chat and the quick-ask window have a speaker on each reply, a "Read answers aloud" switch and a round accent conversation button with a sound-wave glyph beside the dictation microphone, which shows listening, thinking or speaking and sends silence while Scout thinks or speaks ([[pages/services/desktop-quick-ask]]).

Updated: 2026-11-07. The owner, translated: "right now we have the microphone for dictation, I speak and it writes; could we have another icon for conversation?", "and when Scout answers, have an audio that plays what is being written?", "you can put any icon for now, so we can test".

## Decision

- **Read aloud.** Each Scout reply in the chat and in quick ask gets a speaker button that reads it aloud, and a "Read answers aloud" switch reads every reply as it streams, one sentence at a time. Code blocks, tables and links are skipped or named, never spelled out. The speaker button or Esc stops it at once.
- **The voice.** The worker synthesizes speech: the connected AI Source's speech when it has one (the existing `MediaCapabilities.Speech`), otherwise Scout's local voice (the Piper path the operator already uses). One chosen provider: no other account is used. With neither available the button says why.
- **Conversation mode.** A second icon beside the dictation mic. While it is on, Scout listens through the existing live transcription with its pause detection, sends what was heard as an ordinary question on a pause, reads the reply aloud, and listens again. The microphone is muted while Scout speaks, so it never hears itself. A click on the icon, or Esc, ends it. Interrupting Scout by speaking over it (barge-in) is a follow-up.
- **No new write.** Read aloud and conversation mode only answer; every write keeps its grant and approval.

## Amended 2026-11-08: the owner chooses the voice

The owner, translated: "Is there a way to change the voice that speaks?" The owner then approved the Speech tab of the Models prototype (`context/prototypes/desktop/Models.dc.html`).

- **Where.** A Speech tab in Models, after Translation, with two sections: the voice of the AI Source, shown only when the connected AI Source has speech, and Scout's local voices, only those on this machine, the default one marked Default and listed first, the rest collapsed to four with "Show all N". Each row has "Hear a sample"; Save stores the choice, and the line under it says "Saving does not restart anything; the next sentence read uses the new voice." ([[pages/services/desktop-ui]], Models → Speech).
- **The list.** The worker answers a `voices` line with what it can read with: the AI Source's name and its voices (the existing `MediaCapabilities.SpeechVoices`, so the six OpenAI voices live in one place), empty without speech, and the local voices `PiperSpeech` lists, each speaker of a model of several its own voice, with the default one ([[pages/services/adapter-local]], [[pages/services/operator]]).
- **Saving.** The choice is two renderer-writable public vault keys, `speech.voice.provider` and `speech.voice.local`. Main reads both for every sentence it sends to the worker and puts them on the `speak` line, so a change applies to the next sentence with no worker restart ([[pages/services/desktop-quick-ask]]). Read aloud and conversation mode use it the same way.
- **Refusal.** The worker checks the voice of the path it will use, the AI Source's when it has speech, else the local one, against its list and refuses one it does not have with "The voice '<id>' is not available on this machine; choose another in Scout Desktop, Models, Speech.", never passing it on. A saved AI Source voice is not checked while the AI Source has no speech, so switching to such a source keeps reading aloud with the local voice.
- **A sample** reads one fixed sentence through the same `speak` path in the row's voice; a local voice's sample reads with the local voice even when the AI Source has speech. A sample started while another plays stops the first; a failed one shows the worker's reason on its row.
- **No new write.** The provider's text-to-speech call is the one this ADR already has, with a voice parameter; Piper still writes only its one WAV in Scout's speech folder. The vault keys are the owner's own settings.

## Amended 2026-11-08: a conversation that hears in a noisy room and never fails silently

The owner, translated: "Conversation still does not work: I enabled it, spoke, and nothing was captured. No error in the logs." Four causes were reproduced: a turn ended only after 1 s below a fixed RMS of 0.015, so a room at or above that level, which the browser's automatic gain raises in every pause, never ended a turn before the 20 s cut; a click dropped the final the worker flushes on stop; nothing showed what was being heard; and a second listen on the microphone lane, such as a dictation hold, silently replaced the conversation's session, after which main refused every chunk and the refusals were swallowed.

- **Quiet is relative to the room.** A frame is speech when its RMS is at least 0.015 and at least three times the room, the quietest frame of the last 3 s. Three times is about 10 dB: a frame of room noise rarely swings that far above the room's quietest frame, while speech nearer the room than that is hard for Whisper to read anyway. Digital silence, the muted microphone while Scout answers, is not the room. Until 3 s of sound have been heard the room is taken as quiet, as before, and a turn begun then is kept only if it still holds 160 ms of speech for the room learned when it ends, so a noisy room alone never becomes a question; its windows wait until it holds 160 ms three times the quietest frame heard so far, so noise never shows as a partial while speech, which dips between syllables, still does. The room counts as at most 0.05, so music on a meeting lane cannot set a room that hides speech after it; a room louder than about 0.15 RMS never ends a turn before the 20 s cut. The 1 s end, the 20 s cut and the flush are unchanged, for every listen, meetings included ([[pages/services/transcription-whisper]]).
- **No automatic gain.** The conversation's microphone opens with automatic gain off, echo cancellation and noise suppression on, so the room keeps one level between turns. Dictation's microphone is unchanged.
- **A click keeps what was said; Esc drops it.** The Decision above says "A click on the icon, or Esc, ends it." Both still end it. A click while Scout listens also follows the listen until the worker ends it, at most 10 s, and asks what the owner said by then as an ordinary question; with the conversation off, its answer is read aloud only under "Read answers aloud". Esc, and closing the window or the Chat screen, drop what was in flight, including a line a click left draining. A click means the owner has finished speaking; Esc is the cancel key everywhere else, where it also stops reading aloud. Turning the conversation on again while the last listen drains drops that listen's line.
- **What Scout hears shows.** While Scout listens, the partial text of the line being spoken is written after Listening, announced politely to a screen reader.
- **Never silent.** A listen that replaces another on the same lane ends the replaced one with "Another listen took the microphone." ("... the system audio." on that lane), or with no reason when the owner had already stopped it. The conversation shows that reason, the reason of any listen that ends under it ("Scout stopped listening." when there is none), or the reason main refuses its audio for, on the button, and turns off ([[pages/services/desktop-quick-ask]]).
- **The log.** The worker logs each turn end at Information, with why it ended (Quiet, Cap or Flush) and how long it was, never its text, so the Logs screen shows that speech was heard. A listen that ends without any turn logs how loud its loudest frame and its room were, so a microphone too quiet, or one not selected, shows there too.
- **No new write.** Nothing here leaves the machine; the log line carries no speech.

## Amended 2026-11-08: the microphone and the speaker

The owner, translated: "Should we have the device configuration in Settings? That way we can select what we want to use."

- **Where.** An Audio section in Settings with a Microphone picker, a Speaker picker and a Test button ([[pages/services/desktop-ui]], Settings → Audio). Each picker starts with System default; a device without a label is named by its place, "Microphone 1".
- **Who uses the microphone.** Every capture of the owner's voice: dictation, the conversation and a meeting's own lane, through one opener that adds `deviceId: { exact }` to the constraints each already had, the conversation's automatic gain still off. The system audio lane is not a microphone and is untouched ([[pages/services/desktop-quick-ask]], Microphone and speaker).
- **Who uses the speaker.** Read aloud and the voice samples of Models, Speech: the Web Audio context is pointed at the chosen output with `setSinkId` before each sentence. Where the context cannot choose its output the picker is hidden.
- **Saving.** Both choices are kept in renderer storage, `scout:audio-input` and `scout:audio-output`, as "Read answers aloud" already is under `scout:read-aloud`, and read on every open, so a choice made in Settings reaches the quick-ask window's next capture. A device id is meaningful only to this app's renderer on this computer and is not secret, and the worker never needs it, so it is not a vault key: this is not a new storage mechanism, only two more keys of the existing one.
- **Unplugged.** A chosen device that is not connected, found when it is opened or when the system reports a device change, falls back to the system default with one notice, shown only once the default one has opened (with no microphone at all only "No microphone was found." shows), "The chosen microphone is not connected; Scout uses the default one." or "The chosen speaker ...", repeated only after the device came back and went again. The choice is kept, so a replugged device is used again.
- **Distinct errors.** A refused microphone says "Scout was not allowed to use the microphone.", none "No microphone was found.", a busy one "The microphone is busy: another program is using it.", in Settings and wherever a capture starts; any other failure keeps "Scout could not use the microphone.".
- **Test.** Held for at most 10 s, it opens the chosen microphone with the conversation's constraints and shows its level per 20 ms frame, with two marks: the room, the quietest frame of the first second of sound, and the speech threshold over it, max(0.015, 3 x the room counted as at most 0.05), the worker's own constants, which a renderer spec reads from the worker's source so the two cannot drift. Release, or leaving Settings, stops it and releases the microphone. Opening Settings alone never opens the microphone; while the system still hides the devices' ids, before the first capture, a line under the Microphone picker says "Press and hold Test once so Scout can list your microphones and speakers."
- **No new write.** Nothing leaves the machine: the choices stay in renderer storage, and the level is computed in the window and kept nowhere.

## Consequences

- **Better:** the owner can talk with Scout hands-free and hear its answers.
- **Worse:** speech synthesis runs per sentence on the worker; the local voice needs its model downloaded once.
