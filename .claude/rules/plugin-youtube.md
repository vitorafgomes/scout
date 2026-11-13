---
paths:
  - "src/Scout.Plugin.YouTube/**"
  - "tests/Scout.Plugin.YouTube.Tests/**"
---

# Plugin.YouTube

Owns the YouTube Data API v3 client, the kernel functions, reading the token file and appending the API key. Does not own the sign-in, the refresh or the token file itself, which the desktop main process writes ([[pages/services/desktop-ui]]), nor kernel composition ([[pages/services/llm]]), nor the channel operator that searches through this client and runs the approved writes and the analytics reads ([[pages/services/operator]]). The channel operator reads the comments and runs the replies and holds the owner approved through these clients ([[pages/services/operator]]); captions are a later phase ([[pages/roadmap/youtube-creator-assistant]]).

## Rules this project must keep

- [[pages/invariants/no-executive-writes]] as amended by [[pages/decisions/0032-per-feature-write-permissions]]: the model's functions only read; the only HTTP call of their client is `GetAsync`. `tests/Scout.Plugin.YouTube.Tests/YouTubeTokenFileHandlerTests.cs:133` invokes every function with the token file and with a key and fails on any request that is not a GET to `www.googleapis.com`, and on a key outside the query string. The write client is no kernel plugin; only the channel operator calls it, for actions the owner approved one by one ([[pages/services/operator]]), and the reply and the hold are two more of those actions. The comment threads have no model function either: only the operator's comment desk lists them. Neither the session address of an upload nor a report download can send the owner's token off Google's hosts (`tests/Scout.Plugin.YouTube.Tests/YouTubeWriteClientTests.cs:277`, `tests/Scout.Plugin.YouTube.Tests/YouTubeReportingClientTests.cs:130`).
- [[pages/invariants/secrets-never-in-source]]: the key or the token file path comes from configuration only; blank means the plugin is absent. The client logs no requests (`src/Scout.Plugin.YouTube/YouTubeServiceCollectionExtensions.cs:59`), because the key is in the URL and the framework's URL redaction can be switched off; `tests/Scout.Plugin.YouTube.Tests/YouTubeClientTests.cs:325` drives six failures with a key and asserts that no exception message and no log line carries it.
- [[pages/invariants/pluggable-boundaries]]: a `KernelPlugin` singleton picked up by the shared kernel; no Google type leaves the project.

## Pages that govern this project

Read one when your change touches what its title names.

- [[pages/invariants/no-executive-writes|Scout performs descriptive writes only]]
- [[pages/invariants/secrets-never-in-source|Secrets never live in source]]
- [[pages/invariants/pluggable-boundaries|Adapters, LLM providers and plugins are swapped by configuration]]
- [[pages/decisions/0030-youtube-plugin-read-only-with-browser-sign-in|ADR 0030 - YouTube plugin: read-only over the official APIs, signed in from the desktop through the browser]]
- [[pages/decisions/0032-per-feature-write-permissions|ADR 0032 - Features declare what they write, and the owner grants each one]] - the writes run only as actions the owner approved
- [[pages/roadmap/youtube-creator-assistant|YouTube creator assistant]] - the phases still to come
- [[pages/conventions/testing|Testing]]

## What to read for a change

Find the row your change matches and read that page. Read one or two rows, not the table.

| If the change touches | Read |
|---|---|
| Public contract | `context/pages/services/plugin-youtube/public-contract.md` |
| How it works | `context/pages/services/plugin-youtube/how-it-works.md` |
| Hot spots | `context/pages/services/plugin-youtube/hot-spots.md` |
| Testing footprint | `context/pages/services/plugin-youtube/testing-footprint.md` |

For where a type lives or who calls it, ask the code graph (`graphify explain "<Type>"`, `graphify affected "<Type>"`).
