---
title: "Knowledge"
version: "1.13"
date: 2026-10-21
updated: 2026-11-11
changes: "v1.13 (2026-11-11) a folder below the root that cannot be listed is skipped with a warning and the rest indexed; a replace writes the new vectors before it removes the earlier ones, reads every earlier id page by page, and a failed write leaves the vectors the project had."
page_type: service
status: active
description: "Indexes configured repositories, and the repository snapshots the GitHub plugin takes from chat, into a local SQLite file (a vector collection and an FTS5 full-text table) and answers search_project, find_symbol and list_projects tool calls from it, through a Project Expert chat agent."
source: ["src/Scout.Knowledge/", "tests/Scout.Knowledge.Tests/"]
reliability: high
---

# Knowledge

> Reads configured repositories, indexes their text and, when the provider has embeddings, their vectors, and lets the model search them; never writes to them.

## Responsibility

Owns discovery, chunking, indexing, the vector store, the optional file watcher, and the Project Expert agent persona. Does not own the embedding model ([[pages/services/llm]]) or the chat flow ([[pages/services/conversation]]).

## Rules

The rules this project must keep, and what to read before changing it, are in `.claude/rules/knowledge.md`. Claude Code loads that file when a task touches the project.

## Topics

Read the page your change touches, not all of them.

| Page | Read when a change touches | Characters |
|---|---|---|
| [[pages/services/knowledge/public-contract-entry-point\|Public contract: Entry point]] | Entry point; Abstractions; Model tools; Store; Without embeddings; Chunks and ids; Failures while indexing | 11461 |
| [[pages/services/knowledge/public-contract-freshness\|Public contract: Freshness]] | Freshness; Agent; Configuration keys; External boundaries | 4485 |
| [[pages/services/knowledge/how-it-works\|How it works]] | How it works | 1191 |
| [[pages/services/knowledge/hot-spots\|Hot spots]] | Hot spots | 2143 |
| [[pages/services/knowledge/testing-footprint\|Testing footprint]] | Testing footprint | 8391 |

## Related

Direct project references: none. Referenced by [[pages/services/host-console]]. Map: [[pages/architecture/project-map]]. Dated changes: [[pages/history/knowledge]].
