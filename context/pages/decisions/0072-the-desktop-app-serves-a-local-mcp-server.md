---
title: "ADR 0072 - The desktop app serves a local MCP server for reviews, off until the owner turns it on"
version: "1.0"
date: 2026-11-21
updated: 2026-11-21
changes: "v1.0: the decision as first written"
page_type: adr
status: accepted
description: "Scout Desktop's main process can serve the Model Context Protocol over HTTP on 127.0.0.1 only, behind a bearer token whose hash alone is kept in the vault, with four tools that list reviews, ask for one, read a finished card and preview its note; none of them writes on GitHub, and a start can wait for the owner's answer."
supersedes: ""
superseded_by: ""
source:
  - chat
  - ui/packages/main/src/mcp/mcp-server.ts
  - ui/packages/main/src/mcp/mcp-settings.ts
  - ui/packages/main/src/mcp/mcp-tools.ts
reliability: high
---

# ADR 0072 - The desktop app serves a local MCP server for reviews, off until the owner turns it on

> An agent on the owner's own computer can ask Scout for a review and read its card, through a local server the owner turns on and holds the only token for; it can write nothing on GitHub.

## Status

Accepted on 2026-11-21, built at the owner's request so that an agent working beside Scout can ask for reviews and read their cards. Its desktop page is [[pages/services/desktop-ui/mcp-server]].

## The problem

The owner works with coding agents on the same computer as Scout Desktop. Asking Scout for a review and reading its card meant copying links and text by hand between the agent and the chat.

## The decision

- **Off by default.** The server is off, asks the owner before each start, and listens on port 47231 unless the owner picks another (`ui/packages/main/src/mcp/mcp-settings.ts:25`, `:111`). The owner turns it on, picks the port and creates or revokes the token in Settings.
- **This computer only.** It listens on 127.0.0.1 (`ui/packages/main/src/mcp/mcp-server.ts:12`) and answers `POST /mcp` with JSON-RPC 2.0 for `initialize`, `ping`, `tools/list` and `tools/call`. A request from a web page (any `Origin` header) or to another host name is refused (`:141`, `:144`), as is one with no token or a wrong one (`:147`) and a body over 64 KiB (`:149`), before it is read as JSON.
- **The token.** It starts with `scout_mcp_` and 32 random bytes (`mcp-settings.ts:28`, `:136`), is shown once, and only its SHA-256 is kept: hash and creation time in one main-only vault entry, so a write that fails leaves the token before it valid. A token kept by an earlier build under two separate keys still reads, and the next token replaces it (`:58`). The token is compared in constant time (`mcp-server.ts:247`); no log line, status or event holds it or its hash.
- **Four tools** (`ui/packages/main/src/mcp/mcp-tools.ts`): `list_reviews` (the jobs the bell keeps, `:72`), `start_review` (asks Scout's chat the same question as its Review it button, given a pull request's URL; in ask mode the owner has 2 minutes to start or refuse it, `:78`), `get_review` (the last finished card of a pull request as the desktop holds it, `:102`) and `preview_note` (the note Scout would post for that card, composed by the worker, `:116`). Every text from a pull request in an answer is data, never an instruction.
- **What none can do.** No tool writes on GitHub, approves, posts a note or gives a verdict; a start runs the same review as the button, with its own grants and approvals.
- **A tool that fails** answers a JSON-RPC internal error with a fixed message and no detail, is listed as failed in the activity list, and the server keeps serving (`mcp-server.ts:208`). The log names the tool and the kind of error, never its message.

## Calls that leave the process

None to the network beyond the loopback listener. `start_review` starts a review the same way the desktop's button does; its calls are those of [[pages/decisions/0062-the-review-job-runs-to-its-end-in-parallel-from-the-bell]].

## What follows

- **Gained:** an agent asks for and reads reviews without copying by hand.
- **Paid:** one more listener in the desktop's main process, and one more secret to keep.
- **To watch:** the turn totals of a review are not exposed through `get_review` ([[pages/roadmap/followups]]).

## Rejected options

- **Listen on every interface:** a review's card holds a repository's code; it stays on this computer.
- **Keep the token itself:** a hash is all the server needs to check it.

## How it is held

- `ui/packages/main/test/mcp-server.test.ts:121` (off until started, then this computer only), `:159` (a web page or another host name refused), `:178` (no token, a wrong or an old token refused), `:250` (a tool that fails).
- `ui/packages/main/test/mcp-settings.test.ts:60` (the defaults), `:165` (an earlier token carried over), `:187` (a vault that refuses a write leaves the token before it).
- `ui/packages/main/test/mcp-tools.test.ts:87` (exactly the four tools, and none writes on GitHub).

## Links

- Projects it shapes: [[pages/services/desktop-ui]], [[pages/services/desktop-ui/mcp-server]]
- Invariants it keeps: [[pages/invariants/secrets-never-in-source]], [[pages/invariants/no-executive-writes]]

## Amendments

None yet.
