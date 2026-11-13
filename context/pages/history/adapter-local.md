---
title: "Adapter local"
version: "1.0"
date: 2026-11-13
changes: "Version notes moved out of page headers."
page_type: knowledge
status: active
description: "Version notes that were kept in page headers."
source: ["context/pages/"]
---

## Version notes: `pages/services/adapter-local.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- v1.16 (2026-11-11) a live translation the AI Source answers HTTP 404 for its model says the model is not installed, by name, with the conversation's missing-model sentence.
- v1.15 (2026-11-11) a live translation the Claude API refuses for a credit balance too low says the account has run out of credit. A listen whose live start throws is answered with the transcription fail sentence; a failed audio feed stops its session before its fail; the adapter's stop returns only once the stdin reader, the line it is handling and the open live sessions have ended; delete all answers the count the delete returns without listing every meeting; citations into LocalChatAdapter.cs re-pointed by content (2026-11-11). Earlier: A reset first cancels every ask still running, each ending with the Cancelled sentence, then clears the memory, so Clear stops Scout as well as making it forget (2026-11-08); a turn the reset cancelled never comes back into the cleared memory (2026-11-09). Earlier: A speak may name the voice (providerVoice, localVoice, localOnly for a sample), checked against the voices the path in use offers and refused when this machine does not have it; a voices request lists them (ADR 0050 amended, 2026-11-08). Earlier: A keep may carry a keepKey, handed to the store, which keeps once per key; a malformed key is refused before the store (2026-11-08). Earlier: A failure after part of the reply was streamed ends with the could-not-finish sentence alone, after one more update carrying any text that arrived since the last one, so the desktop shows the whole part once (2026-11-07). Earlier: Read aloud: a speak line answered with one audio event from the optional ISpeechSynthesizer, or a fail with the no-voice, could-not or voice's own reason, never logging the sentence (ADR 0050). Earlier: Clear resets the memory: a reset line clears the local conversation through IConversationMemoryStore.Clear, and an ask's context reaches the model and stays out of the stored turn (gap plan G6). Earlier: kept meetings: keep, meetings, export and delete through the optional IMeetingStore and the Markdown renderer delegate, answered as kept, meetings, markdown and deleted, each on a tracked task, with the not-configured, no-meeting, limits and could-not-save sentences and no meeting text in the log (ADR 0037). Earlier: live translation: listen takes translate, each final carries its seq, and a translation event per final from the optional ILiveTranslator through a per-session queue, complete after its drain, a drain that does not wait while paused at the cap, a final refused once the drain started, and auto falling back to en under the invariant culture (ADR 0034, amendment live translation). Earlier: the read request: the text on a screenshot through the optional IScreenTextReader, answered as a text event (ADR 0036)
