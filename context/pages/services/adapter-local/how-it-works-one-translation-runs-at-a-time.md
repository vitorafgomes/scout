---
title: "Adapter.Local: How it works: One translation runs at a time"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Adapter.Local service page."
page_type: service
status: active
description: "Read when a change touches: One translation runs at a time; Context is the two finals spoken; At most 20 translations start in; Each call has 20 s (src/Scout.Adapter.Local/LiveTranslationQueue.cs:21).; Without an ILiveTranslator the listen is;."
source: ["src/Scout.Adapter.Local/", "tests/Scout.Adapter.Local.Tests/", "src/Scout.Host.Console/HostComposition.cs"]
reliability: high
---

## How it works

- One translation runs at a time per session. A final that arrives while one is in flight waits; a newer one replaces it, and the replaced final gets `{seq, language, error: "skipped"}` once the one in flight ends (`src/Scout.Adapter.Local/LiveTranslationQueue.cs:67`, `:91`).

- Context is the two finals spoken before the line, skipped ones included, taken when the final arrives (`src/Scout.Adapter.Local/LiveTranslationQueue.cs:82`).

- At most 20 translations start in any 60 s (`src/Scout.Adapter.Local/LiveTranslationQueue.cs:16`, `:217`). Past that the queue writes `{seq: 0, language, error: "Translation paused: more than 20 lines a minute."}` once per pause, waits until the oldest start leaves the window, and translates the newest waiting final.

- Each call has 20 s (`src/Scout.Adapter.Local/LiveTranslationQueue.cs:21`). A failure writes an `error` for that line and the queue goes on with the next (`:274`): the conversation's rate-limit, quota and sign-in sentences for those three signals (`:282`), the quota code `credit_balance_too_low` taking the conversation's out-of-credit sentence instead, "The AI provider's account has run out of credit. Add credit with the provider, or choose another AI Source in Scout Desktop." (`:285`, `tests/Scout.Adapter.Local.Tests/LiveTranslationQueueTests.cs:236`), a translation model the AI Source does not have (`LlmModelNotFoundException`, an HTTP 404 the translator turns into it with the translation model's id, [[pages/services/llm]]) the conversation's missing-model sentence with that id, "The AI Source does not have the model <id>. A local model is not installed yet or is still downloading: check AI Sources in Scout Desktop." (`:288`, `tests/Scout.Adapter.Local.Tests/LiveTranslationQueueTests.cs:251`), and "Scout could not translate that line; see Scout logs." for anything else, the 20 s timeout included. A failed translation never ends the session.

- Without an `ILiveTranslator` the listen is accepted and transcribes as usual, and the queue writes one `{seq: 0, language, error: "Scout has no AI Source for translation; connect one in AI Sources."}` (`src/Scout.Adapter.Local/LiveTranslationQueue.cs:159`).

- `stop` waits, after the session's last final, at most 15 s for the translation in flight and the one waiting (`src/Scout.Adapter.Local/LiveTranslationQueue.cs:107`, `:126`, called at `src/Scout.Adapter.Local/LocalChatAdapter.cs:654`), then cancels the rest, writes `skipped` for every final left untranslated, and only then `complete` (`:656`). While the queue is paused at the 20-a-minute cap no translation is in flight, so the drain does not wait: it cancels at once and the waiting final is written as `skipped` (`src/Scout.Adapter.Local/LiveTranslationQueue.cs:119`). A final that arrives once the drain has started is refused with a Debug line carrying the id and the seq, and gets no event (`src/Scout.Adapter.Local/LiveTranslationQueue.cs:76`). A failed feed or a failed stop drains the queue at once, with no wait (`src/Scout.Adapter.Local/LocalChatAdapter.cs:630`, `:668`), so the line in flight is written as `skipped` before its `fail` and no translation follows the `fail`; the adapter's lifetime cancels it on shutdown.

- Logs carry the session id, the seq, the language, the length of a translation and the exception type of a failure, never the transcript, the translation or the exception message (`src/Scout.Adapter.Local/LiveTranslationQueue.cs:276`).

**Screenshot text** ([[pages/decisions/0036-text-read-from-a-screenshot-by-a-local-ocr-program]]):

- `read` (`src/Scout.Adapter.Local/LocalChatAdapter.cs:367`, `:716`) checks the image first, by the ask's rule (`:718`): no image, or anything but `image/jpeg` of 1 byte to 3 MB decoded, gets `fail` "The screenshot must be a JPEG of at most 3 MB." and reaches no reader.

- Without an `IScreenTextReader` it gets `fail` "Scout cannot read text from the screen on this machine; the text reader is not installed." (`src/Scout.Adapter.Local/LocalChatAdapter.cs:727`).

- Otherwise the reader runs on a tracked background task with the adapter's lifetime token (`src/Scout.Adapter.Local/LocalChatAdapter.cs:733`), so the reader loop never waits for it and `StopAsync` drains it; a read takes no ask slot and no live session slot, and `cancel` does not apply to it.

- The text is written as one `text` event for the id (`src/Scout.Adapter.Local/LocalChatAdapter.cs:741`); a reader that throws, past its budget, on a failing exit or with no text, ends with `fail` "Scout could not read the text on the screen; see Scout logs." and an Error log line with the id (`:750`, `:751`).

- Logs carry the id, the image's byte count and, once answered, the text's length (`src/Scout.Adapter.Local/LocalChatAdapter.cs:732`, `:742`), never the image or the text.

**Kept meetings** ([[pages/decisions/0037-meetings-kept-locally-in-sqlite]], [[pages/services/meetings]]):

- Without an `IMeetingStore` or the renderer, each of the four requests gets `fail` "Scout keeps no meetings on this machine; the meetings database is not configured." and an Information line with the type and id (`src/Scout.Adapter.Local/LocalChatAdapter.cs:378`, the sentence at `:51`).

- `keep` (`src/Scout.Adapter.Local/LocalChatAdapter.cs:380`) maps the request into a `MeetingRecord` (`:755`): no `meeting`, no `startedAt` or `endedAt`, a line without `at` or `text` or with a lane other than `me` or `others`, or a turn without `at`, `question` or `answer` gets the "could not read" `fail` and reaches no store; missing `lines` or `turns` are empty, and a missing title is empty and fails the limits. A `keepKey`, when present, must be 1 to 64 characters of `[A-Za-z0-9_-]`, else the line gets "Scout could not read that request." and reaches no store; it goes to `IMeetingStore.KeepAsync`, which keeps once per key ([[pages/services/meetings]]). The store's answer is `kept` with the `meetingId` (`src/Scout.Adapter.Local/LocalChatAdapter.cs:791`); an `ArgumentException`, the store refusing a meeting past its limits, is `fail` "A meeting holds at most 5000 lines and 500 turns, with a title of 1 to 200 characters." (`:801`).

- `meetings` (`src/Scout.Adapter.Local/LocalChatAdapter.cs:386`, `:809`) lists at most `limit` meetings, 100 when absent, clamped to 1 to 1,000 (`:55`, `:56`), newest first, as `items`.

- `export` (`src/Scout.Adapter.Local/LocalChatAdapter.cs:390`, `:823`) answers `markdown` with the meeting id and the renderer's text (`:839`); `delete` with a `meetingId` answers `deleted` with `count` 1 (`:396`, `:850`), and with `all: true` deletes them all and answers the count `IMeetingStore.DeleteAllAsync` returns, without listing the meetings (`:393`, `:870`, `:874`; since 2026-11-11, until then every meeting was listed only to be counted). An id that is not 22 base64url characters (`:155`) never reaches the store; it and an id the store does not have get `fail` "Scout has no meeting with that id.". An `export` or `delete` without either field gets the "could not read" `fail`.

- Every request runs on a tracked background task with the adapter's lifetime token, like a `read`, so the reader loop never waits for the database, `StopAsync` drains it, and a keep beside an open listen is answered while the listen goes on; `cancel` does not apply.

- Any other failure is `fail` "Scout could not save or read the meeting; see Scout logs." with an Error line carrying the type, the id and the exception type only (`src/Scout.Adapter.Local/LocalChatAdapter.cs:884`, `:892`); the end of the adapter's lifetime logs an Information line and writes nothing.

- Logs carry the request id, the line and turn counts, the title's length on a refusal, the meeting id, the list's count and limit and the export's length, never a title, a line, a translation, a question or an answer.

**Read aloud** ([[pages/decisions/0050-read-aloud-and-conversation-mode]]):

- `speak` (`src/Scout.Adapter.Local/LocalChatAdapter.Speech.cs:19`) refuses a blank text or one over 2000 characters with "Scout could not read that request." (`:24`), and without an `ISpeechSynthesizer` (the constructor's last optional argument but one, `src/Scout.Adapter.Local/LocalChatAdapter.cs:107`) answers `fail` "No voice: connect an AI Source with speech (OpenAI or Azure OpenAI), or use a Scout Desktop package, which carries Scout's local voice." (`src/Scout.Adapter.Local/LocalChatAdapter.Speech.cs:14`). The voice it names is a `SpeechChoice` of plain strings (`src/Scout.Conversation/Abstractions/ISpeechSynthesizer.cs:11`), so no vendor type reaches the adapter.

- Otherwise the voice runs on a tracked background task with the adapter's lifetime token (`src/Scout.Adapter.Local/LocalChatAdapter.Speech.cs:79`), like a `read`. A speak that names a voice first asks the voice for its list (`ISpeechSynthesizer.VoicesAsync`) and checks the voice of the path it will use (`UnknownVoice`, `:74`): the AI Source's voice when the AI Source lists voices and this is not a local sample, else the local voice when there are local voices; one not in its list is refused with "The voice '<id>' is not available on this machine; choose another in Scout Desktop, Models, Speech." (`SpeechVoices.Unknown`, `src/Scout.Conversation/Abstractions/ISpeechSynthesizer.cs:27`) and never reaches the voice. An AI Source voice is not checked while the AI Source has no speech, so a saved one does not stop reading aloud after a switch to such a source. The audio is one `audio` event (`src/Scout.Adapter.Local/LocalChatAdapter.Speech.cs:97`); no voice on this machine is the same no-voice `fail` (`:93`); a voice that throws `InvalidOperationException` passes its message on (`:109`), anything else is "Scout could not read that aloud; see Scout logs." (`:114`).

- `voices` (`src/Scout.Adapter.Local/LocalChatAdapter.Speech.cs:41`) answers the no-voice `fail` without an `ISpeechSynthesizer`, else one `voices` event from `VoicesAsync` on a tracked task (`:53`), and "Scout could not list its voices; see Scout logs." (`:17`) when the listing throws.

- Logs carry the id, the sentence's length, the voices named, the content type and the byte count, never the sentence.

When the host's parent watchdog is on, it waits on `InputClosed` instead of reading stdin itself ([[pages/services/host-console]]).

A later post ([[pages/decisions/0033-channel-operator-architecture]]) goes through `LocalOriginPoster`, which accepts only the payload `session` and, while the adapter is started (`src/Scout.Adapter.Local/LocalChatAdapter.cs:896`), writes one `notice` event with a null id, the text capped at 32,000 characters and ending in "..." when cut (`src/Scout.Adapter.Local/LocalOriginPoster.cs:21`). Any other payload, a stopped adapter or a stdout write failure returns false with a Warning (`:25`, `:30`, `:41`).
