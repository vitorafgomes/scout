---
paths:
  - "src/Scout.Adapter.Teams/**"
  - "tests/Scout.Adapter.Teams.Tests/**"
---

# Adapter.Teams

Owns turn handling, message translation and the response sink over an abstract activity sender. Does not own the HTTP endpoint, the `CloudAdapter` or the `IBot` that forward Bot Framework requests; the source states these are not registered (`src/Scout.Adapter.Teams/TeamsServiceCollectionExtensions.cs:18`).

## Rules this project must keep

- [[pages/invariants/pluggable-boundaries]]: registers as `IChatAdapter` only when configured.
- [[pages/invariants/secrets-never-in-source]]: the constructor rejects an empty app id (`src/Scout.Adapter.Teams/TeamsChatAdapter.cs:55`).
- [[pages/invariants/no-executive-writes]]: the only platform writes are the reply activities. Teams fills no `ChatMessage.OriginReference` and registers no `IOriginPoster`, so a later progress post to a Teams conversation returns false ([[pages/decisions/0033-channel-operator-architecture]], [[pages/services/conversation]]).

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
| Public contract | `context/pages/services/adapter-teams/public-contract.md` |
| How it works | `context/pages/services/adapter-teams/how-it-works.md` |
| Hot spots | `context/pages/services/adapter-teams/hot-spots.md` |
| Testing footprint | `context/pages/services/adapter-teams/testing-footprint.md` |

For where a type lives or who calls it, ask the code graph (`graphify explain "<Type>"`, `graphify affected "<Type>"`).
