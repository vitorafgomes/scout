---
paths:
  - "src/Scout.Adapter.Discord/**"
  - "tests/Scout.Adapter.Discord.Tests/**"
---

# Adapter.Discord

Owns the Discord gateway session, message filtering, the thread per conversation, the voice-message projection and download, and the response sink. Does not own routing, memory or transcription ([[pages/services/conversation]], [[pages/services/transcription-whisper]]).

## Rules this project must keep

- [[pages/invariants/pluggable-boundaries]]: self-gates on its token and registers as `IChatAdapter`; the voice download is a delegate on `AudioAttachment`, so no Discord type reaches the core.
- [[pages/invariants/secrets-never-in-source]]: the constructor rejects an empty token and names the config route (`src/Scout.Adapter.Discord/DiscordChatAdapter.cs:56`).
- [[pages/invariants/no-executive-writes]]: the platform writes are the thread on the user's message, the reply posts, their edits, the typing state and a later progress post, all in the originating channel or its thread.

## Pages that govern this project

Read one when your change touches what its title names.

- [[pages/invariants/pluggable-boundaries|Adapters, LLM providers and plugins are swapped by configuration]]
- [[pages/invariants/secrets-never-in-source|Secrets never live in source]]
- [[pages/invariants/no-executive-writes|Scout performs descriptive writes only]]
- [[pages/services/conversation|Conversation]]
- [[pages/decisions/0024-voice-messages-transcribed-locally|ADR 0024 - Voice messages are transcribed locally, after the allow-list]]
- [[pages/conventions/testing|Testing]]

## What to read for a change

Find the row your change matches and read that page. Read one or two rows, not the table.

| If the change touches | Read |
|---|---|
| Public contract | `context/pages/services/adapter-discord/public-contract.md` |
| How it works | `context/pages/services/adapter-discord/how-it-works.md` |
| Hot spots | `context/pages/services/adapter-discord/hot-spots.md` |
| Testing footprint | `context/pages/services/adapter-discord/testing-footprint.md` |

For where a type lives or who calls it, ask the code graph (`graphify explain "<Type>"`, `graphify affected "<Type>"`).
