---
title: "Adapter.Local"
version: "1.7"
date: 2026-10-30
updated: 2026-11-04
changes: "Kept meetings: keep, meetings, export and delete through the optional IMeetingStore and the Markdown renderer delegate, answered as kept, meetings, markdown and deleted, each on a tracked task, with the not-configured, no-meeting, limits and could-not-save sentences and no meeting text in the log (ADR 0037). Earlier: live translation: listen takes translate, each final carries its seq, and a translation event per final from the optional ILiveTranslator through a per-session queue, complete after its drain, a drain that does not wait while paused at the cap, a final refused once the drain started, and auto falling back to en under the invariant culture (ADR 0034, amendment live translation). Earlier: the read request: the text on a screenshot through the optional IScreenTextReader, answered as a text event (ADR 0036)"
page_type: service
status: active
description: "Local chat adapter for the desktop's quick-ask window: reads one JSON request per line from the worker's stdin and streams the reply as SCOUT_LOCAL lines on stdout, as the trusted local owner."
source: ["src/Scout.Adapter.Local/", "tests/Scout.Adapter.Local.Tests/", "src/Scout.Host.Console/HostComposition.cs"]
reliability: high
---

# Adapter.Local

> Lets the owner ask Scout from the desktop's quick-ask window, typed or spoken, through the worker's own stdin and stdout.

## To change here, read

- [[pages/decisions/0028-local-quick-ask-assistant]]
- [[pages/decisions/0034-live-transcription-on-the-local-adapter]] - live transcription sessions
- [[pages/decisions/0035-screenshot-attached-to-a-question]] - a screenshot attached to an ask
- [[pages/decisions/0036-text-read-from-a-screenshot-by-a-local-ocr-program]] - the text read from a screenshot
- [[pages/decisions/0037-meetings-kept-locally-in-sqlite]] - the meetings the owner keeps
- [[pages/invariants/no-executive-writes]]
- [[pages/invariants/pluggable-boundaries]]
- [[pages/decisions/0021-allowed-users]] - why the local owner skips the allow-list
- [[pages/services/conversation]]
- [[pages/conventions/testing]]

## Responsibility

Owns reading requests from stdin, turning each into a local-owner `ChatMessage`, cancelling a request, running up to two live transcription sessions at once, answering a read with the text on a screenshot, turning the four meeting requests into calls on the meetings store, and writing reply events to stdout. Does not own routing, memory, transcription or the access rules ([[pages/services/conversation]]), nor the meetings database or the Markdown document ([[pages/services/meetings]]), nor the quick-ask window, the shortcut or the microphone ([[pages/services/desktop-quick-ask]]).

## Public contract

- **Entry point:** `AddLocalAdapter(IServiceCollection, IConfiguration)` (`src/Scout.Adapter.Local/LocalServiceCollectionExtensions.cs:18`). It registers `LocalChatAdapter` as an `IChatAdapter` only when `Scout:Adapters:Local:Enabled` is true (`:23`), and is called by [[pages/services/host-console]].
- **Key types:**
  - `LocalChatAdapter : IChatAdapter, IAsyncDisposable` (`src/Scout.Adapter.Local/LocalChatAdapter.cs:27`), with `OwnerUserId = "local-owner"` and `ChannelId = "local"` (`:30`, `:33`), and `InputClosed`, which completes on end of file or a read failure (`:124`). Its constructor takes an optional `ILiveTranscriber`, an optional `IScreenTextReader`, an optional `ILiveTranslator`, an optional `Func<CancellationToken, Task<string?>>` that answers the channel mandate's language, an optional `IMeetingStore` and an optional `Func<MeetingRecord, string>` that renders a kept meeting as Markdown (`src/Scout.Adapter.Local/LocalChatAdapter.cs:86`, the last two at `:92`), which the host supplies only when a Whisper model is installed ([[pages/services/transcription-whisper]]), when tesseract and its language data are ([[pages/services/operator]]), when a provider other than `None` is configured ([[pages/services/llm]]), when the YouTube channel operator is composed ([[pages/services/host-console]]) and, the store and the renderer together, when the meetings database is configured ([[pages/services/meetings]]).
  - Internal `LiveTranslationQueue` (`src/Scout.Adapter.Local/LiveTranslationQueue.cs:13`), one per live session that asks for a translation.
  - Internal `LocalResponseSink` (`src/Scout.Adapter.Local/LocalResponseSink.cs:10`).
  - Internal `LocalEventWriter` (`src/Scout.Adapter.Local/LocalEventWriter.cs:14`).
  - `LocalOriginPoster : IOriginPoster` (`src/Scout.Adapter.Local/LocalOriginPoster.cs:11`), registered with the adapter (`src/Scout.Adapter.Local/LocalServiceCollectionExtensions.cs:30`).
- **Configuration keys:** `Scout:Adapters:Local:Enabled`, a boolean, not a credential. The desktop sets `SCOUT__ADAPTERS__LOCAL__ENABLED=true`.
- **Wire format:**
  - Requests, one per stdin line:
    - `{"type":"ask","id","text"}`
    - `{"type":"ask","id","audio":{"contentType":"audio/wav","base64"}}`
    - either ask may also carry `"image":{"contentType":"image/jpeg","base64"}`, a screenshot sent with the question ([[pages/decisions/0035-screenshot-attached-to-a-question]])
    - `{"type":"cancel","id"}`
    - `{"type":"listen","id","translate"?}`, `{"type":"audio","id","pcm16":"<base64 PCM16 LE mono 16 kHz>"}`, `{"type":"stop","id"}` ([[pages/decisions/0034-live-transcription-on-the-local-adapter]]); `translate` is a language tag, `auto`, or empty or absent for no translation
    - `{"type":"read","id","image":{"contentType":"image/jpeg","base64"}}`, the text on a screenshot ([[pages/decisions/0036-text-read-from-a-screenshot-by-a-local-ocr-program]])
  - Replies, one per stdout line: `SCOUT_LOCAL {"id","event":"begin"|"progress"|"update"|"complete"|"fail","text"?,"citations"?:[{"label","url"?}]}`. On `progress` the `text` is what Scout is doing right now ("Get pull request file: octocat hello 42 src/Checkout.cs"), written by `LocalResponseSink.ProgressAsync` (`src/Scout.Adapter.Local/LocalResponseSink.cs:51`) between `begin` and the end, a blank one skipped, never after the end (`tests/Scout.Adapter.Local.Tests/LocalChatAdapterTests.cs:100`); ADR 0013 amendment of 2026-11-06.
  - Read reply, one stdout line: `{"id","event":"text","text"}`, or `{"id","event":"fail","text"}`.
  - Meeting requests ([[pages/decisions/0037-meetings-kept-locally-in-sqlite]]): `{"type":"keep","id","meeting":{"startedAt","endedAt","title","lines":[{"at","lane":"me"|"others","text","translation"?,"language"?}],"turns":[{"at","question","answer","screenshot","transcript","screenText"}]}}`, `{"type":"meetings","id","limit"?}`, `{"type":"export","id","meetingId"}`, `{"type":"delete","id","meetingId"}` and `{"type":"delete","id","all":true}`; times are ISO 8601 with an offset.
  - Meeting replies, one stdout line each: `{"id","event":"kept","meetingId"}`, `{"id","event":"meetings","items":[{"id","startedAt","endedAt","title","lineCount","turnCount"}]}`, `{"id","event":"markdown","meetingId","text"}`, `{"id","event":"deleted","count"}`, or `{"id","event":"fail","text"}`.
  - Live session events, one per stdout line: `{"id","event":"partial","text"}`, `{"id","event":"final","text","seq"}` with `seq` counting the session's finals from 1, then `{"id","event":"complete"}` after the stop, or `{"id","event":"fail","text"}`.
  - Translation events, when the listen asked for one: `{"id","event":"translation","seq","text","language"}`, or `{"id","event":"translation","seq","language","error"}`; `seq` is the final's, or 0 for a note about the whole session.
  - A later unsolicited post, outside any turn: `SCOUT_LOCAL {"id":null,"event":"notice","text":"..."}`, no citations. The desktop shows it as its own Scout message in the shared local conversation ([[pages/services/desktop-quick-ask]], `ui/packages/main/src/quick-ask/quick-ask.ts:522`).
- **External boundaries:** the process's stdin (read) and stdout (write). No network of its own: a translation reaches the connected provider through `ILiveTranslator` ([[pages/services/llm]]), and the mandate language the control plane through the host's delegate ([[pages/services/host-console]]).

## How it works

`StartAsync` opens stdin and starts a background read loop (`src/Scout.Adapter.Local/LocalChatAdapter.cs:136`, `:179`). The loop reads chunks of characters and splits them on `\n`, and stops keeping a line once it passes 20 MB (`MaxRequestChars`, `:35`).

`DispatchAsync` (`src/Scout.Adapter.Local/LocalChatAdapter.cs:252`) handles each line, and the loop awaits it before reading the next:
- An oversized line gets `fail` "That request is too large for Scout." with the id found in the line's first 256 characters (`:263`).
- Malformed JSON, or a request with no valid id (1 to 64 letters, digits, hyphens), gets `fail` "Scout could not read that request." with the probed id or `null`.
- `cancel` cancels the matching running request (`:292`).
- `ask` with non-blank text, or with `audio/wav` base64, becomes `ChatMessage("local-owner", "local", ...) { IsLocalOwner = true, OriginReference = "local:session" }` (`src/Scout.Adapter.Local/LocalChatAdapter.cs:366`). The reference names the whole local session, not the request id, because the request has ended by the time a later post arrives ([[pages/decisions/0033-channel-operator-architecture]], [[pages/services/conversation]]). The audio's `OpenAsync` opens a read-only stream over the decoded bytes, with nothing downloaded.
- An `ask` with `image` is checked first (`src/Scout.Adapter.Local/LocalChatAdapter.cs:298`): only `image/jpeg`, compared without case, of 1 byte to 3 MB (3,145,728 bytes) decoded is accepted (`src/Scout.Adapter.Local/LocalChatAdapter.cs:362`, `MaxImageBytes` at `:53`); anything else, on a text ask or a voice ask, gets `fail` "The screenshot must be a JPEG of at most 3 MB." and reaches no handler. An accepted image becomes `ChatMessage.Image`, an `ImageAttachment("image/jpeg", bytes)` over the decoded bytes (`src/Scout.Adapter.Local/LocalChatAdapter.cs:302`). It lives only in that message: no file, no cache. Base64 that does not decode is malformed JSON and gets the "could not read" `fail`.
- `listen`, `audio` and `stop` run a live transcription session; see Live transcription below.
- `read` reads the text on a screenshot; see Screenshot text below.
- `keep`, `meetings`, `export` and `delete` reach the meetings store; see Kept meetings below.
- Anything else gets the same "could not read" `fail`, and a second `ask` with the id of a running one gets "That request is already running.".

The read loop hands each line to `TryDispatchAsync` (`src/Scout.Adapter.Local/LocalChatAdapter.cs:222`), which logs any exception from one request, without its text, and keeps reading, so a cancel that meets a disposed token source or a throwing callback never ends the reader, completes `InputClosed` or trips the watchdog. After a line over 64K characters the reader replaces its buffer instead of keeping it. Every refusal goes through `Refuse` (`src/Scout.Adapter.Local/LocalChatAdapter.cs:350`), which logs a stdout write failure and does not throw, so a broken stdout never ends the reader.

Each accepted request runs on its own task with a token linked to the adapter's lifetime (`src/Scout.Adapter.Local/LocalChatAdapter.cs:393`, `:554`):
- The sink writes `begin`, cumulative `update`s and `complete` with citations.
- A conductor failure becomes `fail`, carrying the partial text plus "(Scout could not finish this answer; see Scout logs)", or "Scout could not answer that; see Scout logs." when there is none (`src/Scout.Adapter.Local/LocalResponseSink.cs:57`).
- A handler that throws ends with `fail` "Scout could not answer that; see Scout logs." (`src/Scout.Adapter.Local/LocalChatAdapter.cs:582`), so the window stops waiting.
- A cancelled request ends with `fail` "Cancelled." unless it already ended (`src/Scout.Adapter.Local/LocalChatAdapter.cs:570`).

`LocalEventWriter.Write` serializes with `System.Text.Json`, which escapes line breaks, and writes one line under `TextWriter.Synchronized` (`src/Scout.Adapter.Local/LocalEventWriter.cs:23`). Over `Console.Out` that is the same lock the readiness and pairing markers use. `StopAsync` cancels the lifetime and waits up to 10 s for running requests. Logs carry the request id, the text length, whether it was voice and whether it carried an image with its byte count (`src/Scout.Adapter.Local/LocalChatAdapter.cs:408`), never the text or the image.

**Live transcription** ([[pages/decisions/0034-live-transcription-on-the-local-adapter]]):
- `listen` (`src/Scout.Adapter.Local/LocalChatAdapter.cs:425`) opens a session on the `ILiveTranscriber` with the adapter's lifetime token, keyed by the request id. Up to two sessions run at once (`MaxLiveSessions`, `:54`), so the desktop can transcribe the microphone and the system audio side by side; which session is which is the desktop's business, and the adapter knows only the ids. Without a transcriber `listen` gets `fail` "Live transcription needs a Whisper model. Download one in the Scout app under AI Sources, Local Models.". A `listen` whose id is already open (`:433`), or a third one while two are open (`:439`), gets `fail` "Scout is already listening." and starts nothing.
- `audio` for an open id (`src/Scout.Adapter.Local/LocalChatAdapter.cs:312`) hands the decoded `pcm16` bytes to that id's session only, in order. The session queues decoding in the background, so the reader never waits for Whisper (`src/Scout.Adapter.Local/LocalChatAdapter.cs:504`). `audio` or `stop` for an id that is not open, or `audio` without bytes, gets the "could not read" `fail`.
- The session's text becomes `partial` and `final` events for that id.
- `stop` (`src/Scout.Adapter.Local/LocalChatAdapter.cs:315`) frees that id's slot at once and, on a tracked background task, waits for the session's last final, then writes `complete` (`src/Scout.Adapter.Local/LocalChatAdapter.cs:527`); the other session keeps running. The adapter's `StopAsync` drains that task like a running request.
- A session that fails, while receiving audio or at the stop, ends with `fail` "Scout could not transcribe that; see Scout logs." and an Error log line with the id. A failed feed frees only its own slot (`src/Scout.Adapter.Local/LocalChatAdapter.cs:516`); the other session keeps going and a new `listen` is accepted in the freed one.
- `cancel` does not apply to a live session.
- Both sessions share the one loaded model and its decode gate ([[pages/services/transcription-whisper]]), so they take turns decoding: a partial of one is dropped while the other holds the model, and finals wait their turn and are never dropped.
- Logs carry the session id, the number of open sessions on start, stop, refusal and a failed feed, and the byte count of each chunk (Debug), never the transcript or the audio.

**Live translation** ([[pages/decisions/0034-live-transcription-on-the-local-adapter]], amendment "live translation"):
- Every `final` carries `seq`, 1 for the session's first final (`src/Scout.Adapter.Local/LocalChatAdapter.cs:467`), with or without a translation.
- A `listen` with a non-blank `translate` gets a `LiveTranslationQueue` (`src/Scout.Adapter.Local/LocalChatAdapter.cs:448`), and each final is handed to it after its `final` line is written (`:468`); the queue never makes the transcription wait.
- Target language (`src/Scout.Adapter.Local/LocalChatAdapter.cs:478`): a tag is used as given; `auto`, in any case, is the mandate's language when the host's delegate answers one, else `CultureInfo.CurrentUICulture.Name` read when the listen arrives (`:480`), and `en` when that name is empty, as under the invariant culture (`LANG=C`), `en` being the local-first default language of the voices and prompts. A delegate that throws, or answers blank, means the interface language, with a Warning naming only the exception type. The mandate's language is free text ("Portuguese"), so `auto` may hand the translator a name rather than a BCP-47 tag; the translator's prompt says "into <language>", so a name works.
- One translation runs at a time per session. A final that arrives while one is in flight waits; a newer one replaces it, and the replaced final gets `{seq, language, error: "skipped"}` once the one in flight ends (`src/Scout.Adapter.Local/LiveTranslationQueue.cs:66`, `:90`).
- Context is the two finals spoken before the line, skipped ones included, taken when the final arrives (`src/Scout.Adapter.Local/LiveTranslationQueue.cs:81`).
- At most 20 translations start in any 60 s (`src/Scout.Adapter.Local/LiveTranslationQueue.cs:15`, `:216`). Past that the queue writes `{seq: 0, language, error: "Translation paused: more than 20 lines a minute."}` once per pause, waits until the oldest start leaves the window, and translates the newest waiting final.
- Each call has 20 s (`src/Scout.Adapter.Local/LiveTranslationQueue.cs:20`). A failure writes an `error` for that line and the queue goes on with the next (`:251`): the conversation's rate-limit, quota and sign-in sentences for those three signals (`:281`), and "Scout could not translate that line; see Scout logs." for anything else, the 20 s timeout included. A failed translation never ends the session.
- Without an `ILiveTranslator` the listen is accepted and transcribes as usual, and the queue writes one `{seq: 0, language, error: "Scout has no AI Source for translation; connect one in AI Sources."}` (`src/Scout.Adapter.Local/LiveTranslationQueue.cs:158`).
- `stop` waits, after the session's last final, at most 15 s for the translation in flight and the one waiting (`src/Scout.Adapter.Local/LiveTranslationQueue.cs:106`, `:125`, called at `src/Scout.Adapter.Local/LocalChatAdapter.cs:534`), then cancels the rest, writes `skipped` for every final left untranslated, and only then `complete` (`:536`). While the queue is paused at the 20-a-minute cap no translation is in flight, so the drain does not wait: it cancels at once and the waiting final is written as `skipped` (`src/Scout.Adapter.Local/LiveTranslationQueue.cs:118`). A final that arrives once the drain has started is refused with a Debug line carrying the id and the seq, and gets no event (`src/Scout.Adapter.Local/LiveTranslationQueue.cs:75`). A failed feed or a failed stop drains the queue at once, with no wait (`src/Scout.Adapter.Local/LocalChatAdapter.cs:521`, `:548`), so the line in flight is written as `skipped` before its `fail` and no translation follows the `fail`; the adapter's lifetime cancels it on shutdown.
- Logs carry the session id, the seq, the language, the length of a translation and the exception type of a failure, never the transcript, the translation or the exception message (`src/Scout.Adapter.Local/LiveTranslationQueue.cs:275`).

**Screenshot text** ([[pages/decisions/0036-text-read-from-a-screenshot-by-a-local-ocr-program]]):
- `read` (`src/Scout.Adapter.Local/LocalChatAdapter.cs:318`, `:596`) checks the image first, by the ask's rule (`:598`): no image, or anything but `image/jpeg` of 1 byte to 3 MB decoded, gets `fail` "The screenshot must be a JPEG of at most 3 MB." and reaches no reader.
- Without an `IScreenTextReader` it gets `fail` "Scout cannot read text from the screen on this machine; the text reader is not installed." (`src/Scout.Adapter.Local/LocalChatAdapter.cs:607`).
- Otherwise the reader runs on a tracked background task with the adapter's lifetime token (`src/Scout.Adapter.Local/LocalChatAdapter.cs:613`), so the reader loop never waits for it and `StopAsync` drains it; a read takes no ask slot and no live session slot, and `cancel` does not apply to it.
- The text is written as one `text` event for the id (`src/Scout.Adapter.Local/LocalChatAdapter.cs:621`); a reader that throws, past its budget, on a failing exit or with no text, ends with `fail` "Scout could not read the text on the screen; see Scout logs." and an Error log line with the id (`:630`, `:631`).
- Logs carry the id, the image's byte count and, once answered, the text's length (`src/Scout.Adapter.Local/LocalChatAdapter.cs:612`, `:622`), never the image or the text.

**Kept meetings** ([[pages/decisions/0037-meetings-kept-locally-in-sqlite]], [[pages/services/meetings]]):
- Without an `IMeetingStore` or the renderer, each of the four requests gets `fail` "Scout keeps no meetings on this machine; the meetings database is not configured." and an Information line with the type and id (`src/Scout.Adapter.Local/LocalChatAdapter.cs:321`, the sentence at `:47`).
- `keep` (`src/Scout.Adapter.Local/LocalChatAdapter.cs:325`) maps the request into a `MeetingRecord` (`:635`): no `meeting`, no `startedAt` or `endedAt`, a line without `at` or `text` or with a lane other than `me` or `others`, or a turn without `at`, `question` or `answer` gets the "could not read" `fail` and reaches no store; missing `lines` or `turns` are empty, and a missing title is empty and fails the limits. The store's answer is `kept` with the new `meetingId` (`src/Scout.Adapter.Local/LocalChatAdapter.cs:665`); an `ArgumentException`, the store refusing a meeting past its limits, is `fail` "A meeting holds at most 5000 lines and 500 turns, with a title of 1 to 200 characters." (`:675`).
- `meetings` (`src/Scout.Adapter.Local/LocalChatAdapter.cs:330`, `:688`) lists at most `limit` meetings, 100 when absent, clamped to 1 to 1,000 (`:51`, `:52`), newest first, as `items`.
- `export` (`src/Scout.Adapter.Local/LocalChatAdapter.cs:334`, `:702`) answers `markdown` with the meeting id and the renderer's text (`:717`); `delete` with a `meetingId` answers `deleted` with `count` 1 (`:340`, `:729`), and with `all: true` counts the meetings through a list, deletes them all and answers that count (`:337`, `:749`, `:753`). An id that is not 22 base64url characters (`:133`) never reaches the store; it and an id the store does not have get `fail` "Scout has no meeting with that id.". An `export` or `delete` without either field gets the "could not read" `fail`.
- Every request runs on a tracked background task with the adapter's lifetime token, like a `read`, so the reader loop never waits for the database, `StopAsync` drains it, and a keep beside an open listen is answered while the listen goes on; `cancel` does not apply.
- Any other failure is `fail` "Scout could not save or read the meeting; see Scout logs." with an Error line carrying the type, the id and the exception type only (`src/Scout.Adapter.Local/LocalChatAdapter.cs:764`, `:772`); the end of the adapter's lifetime logs an Information line and writes nothing.
- Logs carry the request id, the line and turn counts, the title's length on a refusal, the meeting id, the list's count and limit and the export's length, never a title, a line, a translation, a question or an answer.

When the host's parent watchdog is on, it waits on `InputClosed` instead of reading stdin itself ([[pages/services/host-console]]).

A later post ([[pages/decisions/0033-channel-operator-architecture]]) goes through `LocalOriginPoster`, which accepts only the payload `session` and, while the adapter is started (`src/Scout.Adapter.Local/LocalChatAdapter.cs:776`), writes one `notice` event with a null id, the text capped at 32,000 characters and ending in "..." when cut (`src/Scout.Adapter.Local/LocalOriginPoster.cs:21`). Any other payload, a stopped adapter or a stdout write failure returns false with a Warning (`:25`, `:30`, `:41`).

## Invariants

- [[pages/invariants/no-executive-writes]]: the only writes are the reply events to stdout, which the desktop shows in the owner's quick-ask window and Chat screen, the live `partial`, `final`, `translation` and `complete` events, and the later `notice` events, which the desktop drops until it learns the kind. Live audio, an attached screenshot and the text read from one stay in memory. A meeting is written only by the store, to Scout's own data folder, and only on a `keep` ([[pages/services/meetings]]). The adapter adds no tool the model can call, and a `read` or a meeting request comes only from the desktop's stdin.
- [[pages/invariants/pluggable-boundaries]]: references only `Scout.Conversation` and Microsoft.Extensions abstractions. No vendor SDK. Live transcription arrives as `ILiveTranscriber`, never a Whisper type, and the screenshot text as `IScreenTextReader`, never a tesseract type; a translation goes through `ILiveTranslator`, never a Semantic Kernel type; a meeting goes through `IMeetingStore` and its Markdown through a `Func<MeetingRecord, string>`, both over Conversation records, never a Sqlite type, and the project references no `Scout.Meetings`.
- [[pages/invariants/secrets-never-in-source]]: no credential. Request, reply, transcript, translation and screenshot text, screenshot bytes and meeting text are never logged.

## Hot spots

- `src/Scout.Adapter.Local/LocalChatAdapter.cs:366`: the only place `IsLocalOwner` is set to true. Trust comes from holding the worker's stdin ([[pages/decisions/0028-local-quick-ask-assistant]]).
- `src/Scout.Adapter.Local/LocalEventWriter.cs:23`: one `WriteLine` per event is what keeps stdout lines whole.

## Testing footprint

`tests/Scout.Adapter.Local.Tests` (70 test attributes, 104 test cases, 7 files) drives the adapter over an anonymous pipe and captures stdout through `LineCapture.cs`:
- Events in order with the request id and citations.
- Every ask is the local owner in channel `local`.
- A reply containing `\nSCOUT_PAIRED=999\r\nSCOUT_READY` stays in one escaped line.
- Malformed, truncated, oversized, empty, non-WAV and unknown-type lines get `fail` and the next request is answered.
- Cancel ends with "Cancelled." and no `complete`, a duplicate id is refused, and a cancel whose callback throws leaves the reader running.
- A handler that throws ends its reply with `fail`.
- An audio request reaches the handler as a WAV attachment over the same bytes.
- A JPEG screenshot, up to exactly 3 MB and with the content type in any case, reaches the handler as `ChatMessage.Image` over the same bytes (`tests/Scout.Adapter.Local.Tests/LocalChatAdapterTests.cs:310`), on a voice ask too (`:334`); a PNG, an empty image, a missing content type and 3 MB plus one byte, on a text or a voice ask, get the screenshot `fail` and reach no handler (`:364`); the accepted log line has `image=True` and the byte count and no log line holds the image (`:387`).
- A `read` answers one `text` event with the reader's text over the same bytes (`tests/Scout.Adapter.Local.Tests/LocalChatAdapterTests.cs:557`), the not-installed sentence without a reader (`:574`), the screenshot sentence for no image, a PNG, an empty image, a missing content type and 3 MB plus one byte, with the reader never called (`:591`), the could-not-read sentence when the reader throws (`:606`), and a log with the id, the byte count and the text length and neither the text nor the image (`:619`); a read beside an open ask and an open listen is answered while both run and both end as usual (`tests/Scout.Adapter.Local.Tests/LocalLiveSessionTests.cs:380`).
- End of file and a stdin read failure complete `InputClosed`.
- A stdout write failure, during a reply or a refusal, leaves the reader running.
- Registration gated on `Enabled`.
- Live sessions (`LocalLiveSessionTests.cs`, over a fake `ILiveTranscriber`): `audio` bytes reach the session in order, `partial`, `final` and `complete` in that order, no transcript in any log line, the no-model and already-listening sentences, a listen after a stop accepted, `audio` or `stop` for an id that is not listening refused, and a failing session ending with the transcription `fail` and no `complete`. Two sessions: both run at once and each final carries its own id, a third `listen` or a repeated id is refused and starts nothing, audio and stop are routed by id, a failed feed ends only its own session, and an unknown `stop` with two open ends neither. Translation: each final with its seq and a translation event for it (`tests/Scout.Adapter.Local.Tests/LocalLiveSessionTests.cs:443`), `auto` as the mandate language or the interface culture when the mandate is blank, absent, not wired or failing, and `en` under the invariant culture (`:475`), the no-AI-Source note once with transcription going on (`:507`), `complete` after the last translation (`:527`), a failed feed or a failed stop writing the line in flight as `skipped` before its `fail`, with nothing after it once the translation answers (`:549`), a translator that throws never ending the session (`:577`), and neither the transcript, the translation nor an exception message in the log (`:607`).
- The queue (`LiveTranslationQueueTests.cs`, over a gated fake `ILiveTranslator` and `FakeTimeProvider`): one in flight with the middle finals skipped and the newest translated (`tests/Scout.Adapter.Local.Tests/LiveTranslationQueueTests.cs:120`), the two previous finals as context (`:150`), the pause note once past 20 a minute and the resume after the window (`:175`), a thrown error, a 429 and a 20 s timeout each written as the line's error with the queue going on (`:204`), the drain waiting for the line in flight and giving up at 15 s with the gaps written (`:236`), a drain while paused completing without waiting and the waiting line skipped (`:276`), and a final that arrives once the drain started never translated and writing nothing (`:299`).
- Kept meetings, over a fake `IMeetingStore` and a fake renderer: a `keep` storing both lanes with the translation and the turns with their flags and answering its id (`tests/Scout.Adapter.Local.Tests/LocalChatAdapterTests.cs:780`), `meetings` with its limit, `export` with the renderer's text, `delete` one and `delete` all with their counts (`:807`), an unknown or malformed meeting id answered with the no-meeting sentence, the malformed one never reaching the store (`:845`), the not-configured sentence for each request without a store (`:859`, `:875`), the could-not-save sentence for a store that throws (`:887`), the limits sentence for an `ArgumentException` (`:903`), a keep that is not a meeting refused before the store (`:921`), and no title, line, translation, question, answer or exception message in the log, the log helper recording exceptions too (`:942`); a keep held in the store while a listen is open, the listen's finals and complete still written (`tests/Scout.Adapter.Local.Tests/LocalLiveSessionTests.cs:417`).
- Every ask, text or audio, carries `local:session`; a later post writes exactly one `notice` line with a null id, is capped at 32,000 characters, returns false with a Warning when stdout cannot be written, and posts nothing for another payload or a stopped adapter (`LocalOriginPosterTests.cs`).

## Related

Direct project references: [[pages/services/conversation]]. Referenced by [[pages/services/host-console]]. Map: [[pages/architecture/project-map]]. Decisions: [[pages/decisions/0028-local-quick-ask-assistant]], [[pages/decisions/0034-live-transcription-on-the-local-adapter]], [[pages/decisions/0035-screenshot-attached-to-a-question]], [[pages/decisions/0036-text-read-from-a-screenshot-by-a-local-ocr-program]], [[pages/decisions/0037-meetings-kept-locally-in-sqlite]]. The store: [[pages/services/meetings]].
