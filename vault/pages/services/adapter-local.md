---
title: "Adapter.Local"
version: "1.0"
date: 2026-10-30
changes: "New project: the quick-ask adapter over the worker's stdin and stdout (ADR 0028)"
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
- [[pages/invariants/no-executive-writes]]
- [[pages/invariants/pluggable-boundaries]]
- [[pages/decisions/0021-allowed-users]] - why the local owner skips the allow-list
- [[pages/services/conversation]]
- [[pages/conventions/testing]]

## Responsibility

Owns reading requests from stdin, turning each into a local-owner `ChatMessage`, cancelling a request, and writing reply events to stdout. Does not own routing, memory, transcription or the access rules ([[pages/services/conversation]]), nor the quick-ask window, the shortcut or the microphone ([[pages/services/desktop-ui]]).

## Public contract

- **Entry point:** `AddLocalAdapter(IServiceCollection, IConfiguration)` (`src/Scout.Adapter.Local/LocalServiceCollectionExtensions.cs:18`). It registers `LocalChatAdapter` as an `IChatAdapter` only when `Scout:Adapters:Local:Enabled` is true (`:23`), and is called by [[pages/services/host-console]].
- **Key types:**
  - `LocalChatAdapter : IChatAdapter, IAsyncDisposable` (`src/Scout.Adapter.Local/LocalChatAdapter.cs:20`), with `OwnerUserId = "local-owner"` and `ChannelId = "local"` (`:23`, `:26`), and `InputClosed`, which completes on end of file or a read failure (`:65`).
  - Internal `LocalResponseSink` (`src/Scout.Adapter.Local/LocalResponseSink.cs:10`).
  - Internal `LocalEventWriter` (`src/Scout.Adapter.Local/LocalEventWriter.cs:14`).
- **Configuration keys:** `Scout:Adapters:Local:Enabled`, a boolean, not a credential. The desktop sets `SCOUT__ADAPTERS__LOCAL__ENABLED=true`.
- **Wire format:**
  - Requests, one per stdin line:
    - `{"type":"ask","id","text"}`
    - `{"type":"ask","id","audio":{"contentType":"audio/wav","base64"}}`
    - `{"type":"cancel","id"}`
  - Replies, one per stdout line: `SCOUT_LOCAL {"id","event":"begin"|"update"|"complete"|"fail","text"?,"citations"?:[{"label","url"?}]}`.
- **External boundaries:** the process's stdin (read) and stdout (write). No network.

## How it works

`StartAsync` opens stdin and starts a background read loop (`src/Scout.Adapter.Local/LocalChatAdapter.cs:74`, `:117`). The loop reads chunks of characters and splits them on `\n`, and stops keeping a line once it passes 20 MB (`MaxRequestChars`, `:28`).

`Dispatch` (`src/Scout.Adapter.Local/LocalChatAdapter.cs:190`) handles each line:
- An oversized line gets `fail` "That request is too large for Scout." with the id found in the line's first 256 characters (`:197`).
- Malformed JSON, or a request with no valid id (1 to 64 letters, digits, hyphens), gets `fail` "Scout could not read that request." with the probed id or `null`.
- `cancel` cancels the matching running request (`:230`).
- `ask` with non-blank text, or with `audio/wav` base64, becomes `ChatMessage("local-owner", "local", ...) { IsLocalOwner = true }` (`src/Scout.Adapter.Local/LocalChatAdapter.cs:258`). The audio's `OpenAsync` opens a read-only stream over the decoded bytes, with nothing downloaded.
- Anything else gets the same "could not read" `fail`, and a second `ask` with the id of a running one gets "That request is already running.".

The read loop hands each line to `TryDispatch` (`src/Scout.Adapter.Local/LocalChatAdapter.cs:160`), which logs any exception from one request, without its text, and keeps reading, so a cancel that meets a disposed token source or a throwing callback never ends the reader, completes `InputClosed` or trips the watchdog. After a line over 64K characters the reader replaces its buffer instead of keeping it. Every refusal goes through `Refuse` (`src/Scout.Adapter.Local/LocalChatAdapter.cs:246`), which logs a stdout write failure and does not throw, so a broken stdout never ends the reader.

Each accepted request runs on its own task with a token linked to the adapter's lifetime (`src/Scout.Adapter.Local/LocalChatAdapter.cs:285`, `:312`):
- The sink writes `begin`, cumulative `update`s and `complete` with citations.
- A conductor failure becomes `fail`, carrying the partial text plus "(Scout could not finish this answer; see Scout logs)", or "Scout could not answer that; see Scout logs." when there is none (`src/Scout.Adapter.Local/LocalResponseSink.cs:57`).
- A handler that throws ends with `fail` "Scout could not answer that; see Scout logs." (`src/Scout.Adapter.Local/LocalChatAdapter.cs:340`), so the window stops waiting.
- A cancelled request ends with `fail` "Cancelled." unless it already ended (`src/Scout.Adapter.Local/LocalChatAdapter.cs:328`).

`LocalEventWriter.Write` serializes with `System.Text.Json`, which escapes line breaks, and writes one line under `TextWriter.Synchronized` (`src/Scout.Adapter.Local/LocalEventWriter.cs:23`). Over `Console.Out` that is the same lock the readiness and pairing markers use. `StopAsync` cancels the lifetime and waits up to 10 s for running requests. Logs carry the request id, the text length and whether it was voice, never the text.

When the host's parent watchdog is on, it waits on `InputClosed` instead of reading stdin itself ([[pages/services/host-console]]).

## Invariants

- [[pages/invariants/no-executive-writes]]: the only write is the reply events to stdout, which the desktop shows in the owner's quick-ask window and Chat screen. The adapter adds no tool the model can call.
- [[pages/invariants/pluggable-boundaries]]: references only `Scout.Conversation` and Microsoft.Extensions abstractions. No vendor SDK.
- [[pages/invariants/secrets-never-in-source]]: no credential. Request and reply text is never logged.

## Hot spots

- `src/Scout.Adapter.Local/LocalChatAdapter.cs:258`: the only place `IsLocalOwner` is set to true. Trust comes from holding the worker's stdin ([[pages/decisions/0028-local-quick-ask-assistant]]).
- `src/Scout.Adapter.Local/LocalEventWriter.cs:23`: one `WriteLine` per event is what keeps stdout lines whole.

## Testing footprint

`tests/Scout.Adapter.Local.Tests` (16 test attributes, 22 test cases, 3 files) drives the adapter over an anonymous pipe and captures stdout through `LineCapture.cs`:
- Events in order with the request id and citations.
- Every ask is the local owner in channel `local`.
- A reply containing `\nSCOUT_PAIRED=999\r\nSCOUT_READY` stays in one escaped line.
- Malformed, truncated, oversized, empty, non-WAV and unknown-type lines get `fail` and the next request is answered.
- Cancel ends with "Cancelled." and no `complete`, a duplicate id is refused, and a cancel whose callback throws leaves the reader running.
- A handler that throws ends its reply with `fail`.
- An audio request reaches the handler as a WAV attachment over the same bytes.
- End of file and a stdin read failure complete `InputClosed`.
- A stdout write failure, during a reply or a refusal, leaves the reader running.
- Registration gated on `Enabled`.

## Related

Direct project references: [[pages/services/conversation]]. Referenced by [[pages/services/host-console]]. Map: [[pages/architecture/project-map]]. Decision: [[pages/decisions/0028-local-quick-ask-assistant]].
