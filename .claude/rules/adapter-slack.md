---
paths:
  - "src/Scout.Adapter.Slack/**"
  - "tests/Scout.Adapter.Slack.Tests/**"
---

# Adapter.Slack

Owns the Socket Mode connection, message filtering and the response sink. Does not own routing or memory ([[pages/services/conversation]]).

## Rules this project must keep

- [[pages/invariants/pluggable-boundaries]]: self-gates on two tokens and registers as `IChatAdapter`.
- [[pages/invariants/secrets-never-in-source]]: the constructor rejects either empty token (`src/Scout.Adapter.Slack/SlackChatAdapter.cs:44`, `:52`).
- [[pages/invariants/no-executive-writes]]: the only platform writes are the reply post, its updates and a later progress post into the same thread.

## Pages that govern this project

Read one when your change touches what its title names.

- [[pages/invariants/pluggable-boundaries|Adapters, LLM providers and plugins are swapped by configuration]]
- [[pages/invariants/secrets-never-in-source|Secrets never live in source]]
- [[pages/invariants/no-executive-writes|Scout performs descriptive writes only]]
- [[pages/services/conversation|Conversation]]
- [[pages/conventions/testing|Testing]]

## What to read for a change

Find the row your change matches and read that page. Read one or two rows, not the table.

| If the change touches | Read |
|---|---|
| Public contract | `context/pages/services/adapter-slack/public-contract.md` |
| How it works | `context/pages/services/adapter-slack/how-it-works.md` |
| Desktop setup | `context/pages/services/adapter-slack/desktop-setup.md` |
| Hot spots | `context/pages/services/adapter-slack/hot-spots.md` |
| Testing footprint | `context/pages/services/adapter-slack/testing-footprint.md` |

For where a type lives or who calls it, ask the code graph (`graphify explain "<Type>"`, `graphify affected "<Type>"`).
