---
title: "Adapter.Local: How it works: StartAsync opens stdin and starts a"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Adapter.Local service page."
page_type: service
status: active
description: "Read when a change touches: StartAsync opens stdin and starts a; DispatchAsync (src/Scout.Adapter.Local/LocalChatAdapter.cs:295) handles each line, and; An oversized line gets fail That; Malformed JSON, or a request with; cancel cancels the."
source: ["src/Scout.Adapter.Local/", "tests/Scout.Adapter.Local.Tests/", "src/Scout.Host.Console/HostComposition.cs"]
reliability: high
---

## How it works

`StartAsync` opens stdin and starts a background read loop (`src/Scout.Adapter.Local/LocalChatAdapter.cs:162`, `:222`). The loop reads chunks of characters and splits them on `\n`, and stops keeping a line once it passes 20 MB (`MaxRequestChars`, `:39`).

`DispatchAsync` (`src/Scout.Adapter.Local/LocalChatAdapter.cs:295`) handles each line, and the loop awaits it before reading the next:

- An oversized line gets `fail` "That request is too large for Scout." with the id found in the line's first 256 characters (`:303`).

- Malformed JSON, or a request with no valid id (1 to 64 letters, digits, hyphens), gets `fail` "Scout could not read that request." with the probed id or `null`.

- `cancel` cancels the matching running request (`:341`).

- `reset`, read before the id check (`src/Scout.Adapter.Local/LocalChatAdapter.cs:326`), first cancels every ask still running (a `cancel` repeated for an ask already cancelled or finished is ignored, with no second `fail` line, `tests/Scout.Adapter.Local.Tests/LocalChatAdapterTests.cs:394`), each of which ends with `fail` "Cancelled." like a `cancel` (since 2026-11-08; until then a reset left a running answer going, so Clear never stopped Scout), then clears the conversation of `local-owner` in channel `local` in the memory store under the key the key provider derives (`Reset`, `src/Scout.Adapter.Local/LocalChatAdapter.cs:406`) and writes nothing else to stdout; without a store it still cancels and logs that the clear was ignored. Every ask is from the local owner in the one local channel, so every running ask belongs to the conversation the reset clears. A cancelled ask stores nothing: the router keeps a turn only once its stream completes, and since 2026-11-09 it hands the turn's token to the memory store, which checks it under the conversation's lock before storing (`src/Scout.Conversation/Memory/InMemoryConversationMemoryStore.cs:94`), so a turn whose stream completed in the instant before the cancel landed is not stored after the clear either, the reset cancelling before it clears. The desktop sends it when the owner clears the conversation, after a `cancel` for the running turn, so the next question starts with an empty history ([[pages/services/conversation]], [[pages/services/desktop-quick-ask]]).

- A non-empty `context` on a text ask becomes `ChatMessage.Context` (`src/Scout.Adapter.Local/LocalChatAdapter.cs:479`): the router sends it to the model after the question and stores only the question in memory ([[pages/services/conversation]]).

- `ask` with non-blank text, or with `audio/wav` base64, becomes `ChatMessage("local-owner", "local", ...) { IsLocalOwner = true, OriginReference = "local:session" }` (`src/Scout.Adapter.Local/LocalChatAdapter.cs:451`). The reference names the whole local session, not the request id, because the request has ended by the time a later post arrives ([[pages/decisions/0033-channel-operator-architecture]], [[pages/services/conversation]]). The audio's `OpenAsync` opens a read-only stream over the decoded bytes, with nothing downloaded.

- An `ask` with `image` is checked first (`src/Scout.Adapter.Local/LocalChatAdapter.cs:347`): only `image/jpeg`, compared without case, of 1 byte to 3 MB (3,145,728 bytes) decoded is accepted (`src/Scout.Adapter.Local/LocalChatAdapter.cs:447`, `MaxImageBytes` at `:57`); anything else, on a text ask or a voice ask, gets `fail` "The screenshot must be a JPEG of at most 3 MB." and reaches no handler. An accepted image becomes `ChatMessage.Image`, an `ImageAttachment("image/jpeg", bytes)` over the decoded bytes (`src/Scout.Adapter.Local/LocalChatAdapter.cs:354`). It lives only in that message: no file, no cache. Base64 that does not decode is malformed JSON and gets the "could not read" `fail`.

- `listen`, `audio` and `stop` run a live transcription session; see Live transcription below.

- `read` reads the text on a screenshot; see Screenshot text below.

- `speak` reads one sentence aloud (`src/Scout.Adapter.Local/LocalChatAdapter.cs:370`) and `voices` lists the voices (`:373`); see Read aloud below.

- `keep`, `meetings`, `export` and `delete` reach the meetings store; see Kept meetings below.

- Anything else gets the same "could not read" `fail`, and a second `ask` with the id of a running one gets "That request is already running.".

The read loop hands each line to `TryDispatchAsync` (`src/Scout.Adapter.Local/LocalChatAdapter.cs:265`), which logs any exception from one request, without its text, and keeps reading, so a cancel that meets a disposed token source or a throwing callback never ends the reader, completes `InputClosed` or trips the watchdog. After a line over 64K characters the reader replaces its buffer instead of keeping it. Every refusal goes through `Refuse` (`src/Scout.Adapter.Local/LocalChatAdapter.cs:435`), which logs a stdout write failure and does not throw, so a broken stdout never ends the reader.

Each accepted request runs on its own task with a token linked to the adapter's lifetime (`src/Scout.Adapter.Local/LocalChatAdapter.cs:489`, `:674`):

- The sink writes `begin`, cumulative `update`s and `complete` with citations.

- A conductor failure becomes `fail`. When part of the reply already went out as an `update`, the `fail` carries only "(Scout could not finish this answer; see Scout logs)", since the desktop already shows that part (2026-11-07; until then it repeated the partial text before the sentence). Because the pump sends an `update` at most once per edit interval, text that arrived after the last one would be lost, so when the last `update` does not start with the text the conductor hands over, the sink first writes one more `update` with that text; an empty or shorter text, the conductor's failure after streaming for example, leaves the shown reply as it is; when nothing was streamed it carries the partial text, a voice reply's quote for example, a blank line and that sentence, or "Scout could not answer that; see Scout logs." when there is no text (`src/Scout.Adapter.Local/LocalResponseSink.cs:70`). The interim `update`s are unchanged. Tests: `tests/Scout.Adapter.Local.Tests/LocalChatAdapterTests.cs:461`, `:487`, `:504`, `:524`.

- A handler that throws ends with `fail` "Scout could not answer that; see Scout logs." (`src/Scout.Adapter.Local/LocalChatAdapter.cs:702`), so the window stops waiting.

- A cancelled request ends with `fail` "Cancelled." unless it already ended (`src/Scout.Adapter.Local/LocalChatAdapter.cs:685`).

`LocalEventWriter.Write` serializes with `System.Text.Json`, which escapes line breaks, and writes one line under `TextWriter.Synchronized` (`src/Scout.Adapter.Local/LocalEventWriter.cs:23`). Over `Console.Out` that is the same lock the readiness and pairing markers use. `StopAsync` (`src/Scout.Adapter.Local/LocalChatAdapter.cs:180`) cancels the lifetime and waits up to 10 s for the stdin reader to end, the line it is handling included; a pending read is left behind at the cancel, because a read of the console's stdin does not see a cancellation (`:234`). It then stops every live session still open and waits up to 10 s more for running requests and those stops, so it returns only once the reader and the live sessions have ended (since 2026-11-11; until then it waited for the requests alone, and the reader and the open sessions ended only on the lifetime token). Logs carry the request id, the text length, whether it was voice and whether it carried an image with its byte count (`src/Scout.Adapter.Local/LocalChatAdapter.cs:497`), never the text or the image.

**Live transcription** ([[pages/decisions/0034-live-transcription-on-the-local-adapter]]):

- `listen` (`src/Scout.Adapter.Local/LocalChatAdapter.cs:515`) opens a session on the `ILiveTranscriber` with the adapter's lifetime token, keyed by the request id. Up to two sessions run at once (`MaxLiveSessions`, `:58`), so the desktop can transcribe the microphone and the system audio side by side; which session is which is the desktop's business, and the adapter knows only the ids. Without a transcriber `listen` gets `fail` "Live transcription needs a Whisper model. Download one in the Scout app under Models, Transcription.". A `listen` whose id is already open (`:523`), or a third one while two are open (`:529`), gets `fail` "Scout is already listening." and starts nothing. A transcriber whose `Start` throws gets `fail` "Scout could not transcribe that; see Scout logs." and an Error log line with the id, takes no slot and starts no translation queue (`src/Scout.Adapter.Local/LocalChatAdapter.cs:557`, since 2026-11-11; until then only the reader logged it and the desktop waited for an answer that never came).

- `audio` for an open id (`src/Scout.Adapter.Local/LocalChatAdapter.cs:361`) hands the decoded `pcm16` bytes to that id's session only, in order. The session queues decoding in the background, so the reader never waits for Whisper (`src/Scout.Adapter.Local/LocalChatAdapter.cs:606`). `audio` or `stop` for an id that is not open, or `audio` without bytes, gets the "could not read" `fail`.

- The session's text becomes `partial` and `final` events for that id.

- `stop` (`src/Scout.Adapter.Local/LocalChatAdapter.cs:364`) frees that id's slot at once and, on a tracked background task, waits for the session's last final, then writes `complete` (`src/Scout.Adapter.Local/LocalChatAdapter.cs:656`); the other session keeps running. The adapter's `StopAsync` drains that task like a running request.

- A session that fails, while receiving audio or at the stop, ends with `fail` "Scout could not transcribe that; see Scout logs." and an Error log line with the id. A failed feed frees only its own slot (`src/Scout.Adapter.Local/LocalChatAdapter.cs:618`); the other session keeps going and a new `listen` is accepted in the freed one. On a tracked task the failed session is then stopped through `ILiveSession.StopAsync`, a failure of that stop logged as Information with the exception type only, before its translation drains and its `fail` is written (`src/Scout.Adapter.Local/LocalChatAdapter.cs:625`, `:635`; since 2026-11-11, until then the session was dropped without its stop).

- `cancel` does not apply to a live session.

- Both sessions share the one loaded model and its decode gate ([[pages/services/transcription-whisper]]), so they take turns decoding: a partial of one is dropped while the other holds the model, and finals wait their turn and are never dropped.

- Logs carry the session id, the number of open sessions on start, stop, refusal and a failed feed, and the byte count of each chunk (Debug), never the transcript or the audio.

**Live translation** ([[pages/decisions/0034-live-transcription-on-the-local-adapter]], amendment "live translation"):

- Every `final` carries `seq`, 1 for the session's first final (`src/Scout.Adapter.Local/LocalChatAdapter.cs:551`), with or without a translation.

- A `listen` with a non-blank `translate` gets a `LiveTranslationQueue`, built once the session has started (`src/Scout.Adapter.Local/LocalChatAdapter.cs:564`), and each final is handed to it after its `final` line is written (`:552`); the queue never makes the transcription wait.

- Target language (`src/Scout.Adapter.Local/LocalChatAdapter.cs:580`): a tag is used as given; `auto`, in any case, is the mandate's language when the host's delegate answers one, else `CultureInfo.CurrentUICulture.Name` read when the listen arrives (`:582`), and `en` when that name is empty, as under the invariant culture (`LANG=C`), `en` being the local-first default language of the voices and prompts. A delegate that throws, or answers blank, means the interface language, with a Warning naming only the exception type. The mandate's language is free text ("Portuguese"), so `auto` may hand the translator a name rather than a BCP-47 tag; the translator's prompt says "into <language>", so a name works.
