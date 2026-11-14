---
title: "Adapter.Local: Public contract"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Adapter.Local service page."
page_type: service
status: active
description: "Read when a change touches: Public contract."
source: ["src/Scout.Adapter.Local/", "tests/Scout.Adapter.Local.Tests/", "src/Scout.Host.Console/HostComposition.cs"]
reliability: high
---

## Public contract

- **Entry point:** `AddLocalAdapter(IServiceCollection, IConfiguration)` (`src/Scout.Adapter.Local/LocalServiceCollectionExtensions.cs:18`). It registers `LocalChatAdapter` as an `IChatAdapter` only when `Scout:Adapters:Local:Enabled` is true (`:23`), and is called by [[pages/services/host-console]].
- **Key types:**
  - `LocalChatAdapter : IChatAdapter, IAsyncDisposable` (`src/Scout.Adapter.Local/LocalChatAdapter.cs:31`), with `OwnerUserId = "local-owner"` and `ChannelId = "local"` (`:34`, `:37`), and `InputClosed`, which completes on end of file, a read failure or the adapter's stop (`:147`). Its constructor takes an optional `ILiveTranscriber`, an optional `IScreenTextReader`, an optional `ILiveTranslator`, an optional `Func<CancellationToken, Task<string?>>` that answers the channel mandate's language, an optional `IMeetingStore` and an optional `Func<MeetingRecord, string>` that renders a kept meeting as Markdown (`src/Scout.Adapter.Local/LocalChatAdapter.cs:97`, the last two at `:103`), and an optional `IConversationMemoryStore` with its `IConversationKeyProvider`, which a `reset` clears (`src/Scout.Adapter.Local/LocalChatAdapter.cs:105`), and the router's `ICarriedSkills`, whose carried skills the same `reset` forgets by the same key (`src/Scout.Adapter.Local/LocalChatAdapter.cs:431`, [[pages/decisions/0054-functions-scoped-per-skill-and-job]], `tests/Scout.Adapter.Local.Tests/LocalChatAdapterTests.cs:227`), which the host supplies only when a Whisper model is installed ([[pages/services/transcription-whisper]]), when tesseract and its language data are ([[pages/services/operator]]), when a provider other than `None` is configured ([[pages/services/llm]]), when the YouTube channel operator is composed ([[pages/services/host-console]]) and, the store and the renderer together, when the meetings database is configured ([[pages/services/meetings]]).
  - Internal `LiveTranslationQueue` (`src/Scout.Adapter.Local/LiveTranslationQueue.cs:14`), one per live session that asks for a translation.
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
    - `{"type":"reset"}`, no id and no answer: forget the local conversation (gap plan G6)
    - a text ask may also carry `"context"`, the transcript, the text on the screen, the last answer or the meeting text the desktop sends with the question (gap plan G6)
    - `{"type":"listen","id","translate"?}`, `{"type":"audio","id","pcm16":"<base64 PCM16 LE mono 16 kHz>"}`, `{"type":"stop","id"}` ([[pages/decisions/0034-live-transcription-on-the-local-adapter]]); `translate` is a language tag, `auto`, or empty or absent for no translation
    - `{"type":"read","id","image":{"contentType":"image/jpeg","base64"}}`, the text on a screenshot ([[pages/decisions/0036-text-read-from-a-screenshot-by-a-local-ocr-program]])
    - `{"type":"speak","id","text","providerVoice"?,"localVoice"?,"localOnly"?}`, one sentence of a reply, 1 to 2000 characters, read aloud in the voice named, if any: an AI Source voice, a local voice id, and `localOnly` for a sample of the local voice ([[pages/decisions/0050-read-aloud-and-conversation-mode]], Amended 2026-11-08)
    - `{"type":"voices","id"}`, the voices this machine can read with
  - Replies, one per stdout line: `SCOUT_LOCAL {"id","event":"begin"|"progress"|"update"|"complete"|"fail","text"?,"citations"?:[{"label","url"?}]}`. On `progress` the `text` is what Scout is doing right now ("Get pull request file: octocat hello 42 src/Checkout.cs"), written by `LocalResponseSink.ProgressAsync` (`src/Scout.Adapter.Local/LocalResponseSink.cs:51`) between `begin` and the end, a blank one skipped, never after the end (`tests/Scout.Adapter.Local.Tests/LocalChatAdapterTests.cs:111`); ADR 0013 amendment of 2026-11-06.
  - Read reply, one stdout line: `{"id","event":"text","text"}`, or `{"id","event":"fail","text"}`.
  - Speak reply, one stdout line: `{"id","event":"audio","contentType":"audio/mpeg"|"audio/wav","base64"}` (`src/Scout.Adapter.Local/LocalEventWriter.cs:36`), or `{"id","event":"fail","text"}`.
  - Voices reply, one stdout line: `{"id","event":"voices","provider","providerVoices":["alloy",...],"localVoices":[{"id","label"}],"defaultLocal"?}` (`src/Scout.Adapter.Local/LocalEventWriter.cs:39`), or `{"id","event":"fail","text"}`.
  - Meeting requests ([[pages/decisions/0037-meetings-kept-locally-in-sqlite]]): `{"type":"keep","id","keepKey"?,"meeting":{"startedAt","endedAt","title","lines":[{"at","lane":"me"|"others","text","translation"?,"language"?}],"turns":[{"at","question","answer","screenshot","transcript","screenText"}]}}`, `{"type":"meetings","id","limit"?}`, `{"type":"export","id","meetingId"}`, `{"type":"delete","id","meetingId"}` and `{"type":"delete","id","all":true}`; times are ISO 8601 with an offset.
  - Meeting replies, one stdout line each: `{"id","event":"kept","meetingId"}`, `{"id","event":"meetings","items":[{"id","startedAt","endedAt","title","lineCount","turnCount"}]}`, `{"id","event":"markdown","meetingId","text"}`, `{"id","event":"deleted","count"}`, or `{"id","event":"fail","text"}`.
  - Live session events, one per stdout line: `{"id","event":"partial","text"}`, `{"id","event":"final","text","seq"}` with `seq` counting the session's finals from 1, then `{"id","event":"complete"}` after the stop, or `{"id","event":"fail","text"}`.
  - Translation events, when the listen asked for one: `{"id","event":"translation","seq","text","language"}`, or `{"id","event":"translation","seq","language","error"}`; `seq` is the final's, or 0 for a note about the whole session.
  - A later unsolicited post, outside any turn: `SCOUT_LOCAL {"id":null,"event":"notice","text":"..."}`, no citations. The desktop shows it as its own Scout message in the shared local conversation ([[pages/services/desktop-quick-ask]]): main parses it in `toLocalEvent` (`ui/packages/main/src/quick-ask/quick-ask.ts:668`) and appends it as its own turn in the worker-line handler (`:1827`).
- **External boundaries:** the process's stdin (read) and stdout (write). No network of its own: a translation reaches the connected provider through `ILiveTranslator` ([[pages/services/llm]]), and the mandate language the control plane through the host's delegate ([[pages/services/host-console]]).
