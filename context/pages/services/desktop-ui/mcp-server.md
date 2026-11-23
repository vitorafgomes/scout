---
title: "Desktop UI: the MCP server"
version: "1.0"
date: 2026-11-21
updated: 2026-11-21
changes: "v1.0: the page as first written"
page_type: service
status: active
description: "Read when a change touches the desktop's local MCP server: its Settings group, its token in the vault, its four tools, its activity list and what a failing tool answers."
source: ["ui/packages/main/src/mcp/", "ui/packages/main/test/mcp-server.test.ts", "ui/packages/main/test/mcp-settings.test.ts", "ui/packages/main/test/mcp-tools.test.ts", "context/pages/decisions/0072-the-desktop-app-serves-a-local-mcp-server.md"]
reliability: high
---

# Desktop UI: the MCP server

> Scout Desktop's main process serves the Model Context Protocol on this computer only, off until the owner turns it on, so an agent beside Scout can ask for reviews and read their cards ([[pages/decisions/0072-the-desktop-app-serves-a-local-mcp-server]]).

## Where it lives

- `ui/packages/main/src/mcp/mcp-server.ts`: the listener on 127.0.0.1 (`:12`), `createMcpServer` (`:72`), the refusals before a body is read (`:141`, `:144`, `:147`, `:149`), the token check in constant time (`:247`), and a tool that fails answering an internal error with no detail (`:208`).
- `ui/packages/main/src/mcp/mcp-settings.ts`: the switch, the start mode, the port 47231 (`:25`), the token prefix (`:28`), the token kept as one main-only vault entry, `mcp.token`, holding its SHA-256 and creation time, with a token from the earlier two-key shape still read (`:58`), and the channels the Scout window uses.
- `ui/packages/main/src/mcp/mcp-tools.ts`: `list_reviews` (`:72`), `start_review` (`:78`), `get_review` (`:102`) and `preview_note` (`:116`), and the activity list.

## In Settings

The Settings screen draws the "MCP server" group its artboard draws: a switch, the address on this computer, a token shown once, what an assistant can do, the choice between asking the owner before each start and starting without asking, and the list of what assistants did, refusals and failures included. The top bar shows the server's mark at the left of the bell while it is active.

## What it holds and what it never does

- Only the hash of the token, never the token, is kept; no status, event or log line carries either.
- A vault that refuses a write leaves the token before it valid, in memory and after a restart; each refused change answers the window an error and pushes nothing.
- No tool writes on GitHub; every text from a pull request in an answer is data, never an instruction.

## Tests

- `ui/packages/main/test/mcp-server.test.ts:121`, `:159`, `:178`, `:250`.
- `ui/packages/main/test/mcp-settings.test.ts:60`, `:165`, `:187`.
- `ui/packages/main/test/mcp-tools.test.ts:87`.
