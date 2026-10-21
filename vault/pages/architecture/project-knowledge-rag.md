---
title: "Project Knowledge RAG - Design"
version: "1.0"
date: 2026-05-29
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: longform
status: stub
description: "Design for letting a user ask Scout natural-language questions about a software project and get answers grounded in that project's real code and docs."
source:
  - "obsidian-vault/scout/02-architecture/project-knowledge-rag.md"
reliability: medium
---

# Project Knowledge RAG - Design

## Purpose

Let a user ask Scout, in natural language, about a software project — its
**architecture**, **business rules**, and **how something is implemented** — and
get an answer grounded in that project's real **code and in-repo documentation**,
with citations to the source files.

Reference implementation studied and chosen to mirror: `~/WorkSpace/Aperant`
(incremental AST indexing + knowledge graph + local vector store + Ollama
embeddings). We adopt Aperant's full indexing approach.

## Scope

**In scope:**
- A shared **RAG foundation**, Aperant-style:
  - **Automatic incremental indexing** (file-watch + debounce + staleness).
  - **AST chunking** (semantic chunks at function/class boundaries).
  - **Knowledge graph** (symbol nodes + dependency edges + transitive closure).
  - **Vector store** (local, persistent) + dense retrieval with **graph boost**.
- **Feature A — Project Knowledge**: an SK skill + a `Project Expert` agent that
  answers questions about a configured project, with citations.

**Out of scope (deferred):**
- Agent cross-session memory (separate spec — reuses this foundation).
- Multi-agent orchestration / agent-router (single agent for now).
- BM25 keyword fusion + cross-encoder reranking (dense + graph for now; the
  Aperant-grade hybrid retrieval is the next retrieval iteration).

> Source correction: **no dependency on the Obsidian vault.** Each project is
> self-contained in its own repo; "docs" means in-repo markdown (README, `*.md`,
> a `docs/` folder). The vault is the human knowledge base, not a runtime source.

## Configuration

A configured set of projects, referenced by name. Just the repo path:

```json
"Scout": {
  "Projects": [
    { "Name": "cegos-buddy", "RepoPath": "<redacted>" }
  ]
}
```

## Architecture

Two layers on the existing `Scout.Llm` Ollama embedding generator.

### Foundation (shared, Aperant-style)

- **File watcher / incremental indexer**: watches each configured repo for changes
  to source + markdown files (debounced). On change: re-parse the file, mark its
  graph edges stale, upsert fresh nodes/chunks/embeddings, drop removed ones. Skips
  `bin/obj/.git/node_modules/dist`. Runs in the worker; bounded so it never blocks
  chat.
- **AST chunker**: chunks files at function/class/type boundaries, capturing
  `{ symbol name, kind, startLine, endLine, language }` + a context prefix
  (`File: x | class: Y | lines: a-b`) prepended at embed time (Aperant's contextual
  embedding trick). **Multi-language is the key design risk** — see Risks.
- **Knowledge graph**: nodes = symbols/files; edges = contains / references /
  depends-on; a **closure table** gives O(1) transitive-dependency lookup for impact
  and neighborhood retrieval.
- **Vector store**: local, persistent, no external service (SQLite-vec or libSQL-style
  native vectors). Record: `{ project, path, kind, symbol, chunkText, startLine,
  endLine, embedding, modelId, dims }`.
- **Retrieval**: dense top-K cosine (filtered by project) **+ graph-neighborhood
  boost** (chunks linked to already-relevant symbols rank higher). Returns chunks
  with source citations. (BM25 + rerank = next iteration.)

### Feature A — Project Knowledge

- **Skill (SK plugin)** `ProjectKnowledge`:
  `[KernelFunction("search_project")] Task<string> SearchAsync(string project, string question, ...)`
  — embeds the question, retrieves top-K (dense + graph boost) for that project,
  returns a citation-tagged context block. Clean message when the project is unknown /
  not yet indexed / empty. Never throws into the kernel.
- **Agent** `ChatCompletionAgent` "Project Expert": instructions = "answer about the
  user's projects (architecture, business rules, implementation); always ground
  answers in retrieved context and cite the source file; if context does not cover it,
  say so." Skills: `ProjectKnowledge` (+ existing GitHub skill). In v1 this agent is
  the single chat responder; orchestration is deferred.

## Data flow

```
watch repo -> file changed -> AST parse -> chunk (+context prefix)
            -> embed (Ollama) -> upsert vector + graph nodes/edges (staleness)
ask (chat): question -> Project Expert agent -> search_project(project, q)
            -> embed q -> dense top-K (project filter) + graph boost -> cited context
            -> agent composes grounded answer (streaming)
```

## Error handling

- Unknown / unindexed project → skill returns a clear message, never throws.
- Parse/embed/store failure on a file → log + isolate that file, keep indexing the rest.
- Watcher failure → degrade to last good index; surface a health signal; chat unaffected.
- Retrieval failure → clean error string to the agent; host never crashes.

## Testing strategy (TDD, Shouldly)

- AST chunker: function/class boundaries per language; fallback for unsupported; metadata.
- Incremental indexer: change → re-chunk + upsert; delete → removal; debounce; staleness.
- Graph: node/edge build; closure-table transitive lookup; neighborhood query.
- Vector store: upsert + top-K cosine + project filter (boundary: store unavailable).
- Retrieval: dense + graph boost ordering; citations present.
- `ProjectKnowledge` skill: cited context; unknown/unindexed message; retrieval failure
  → clean string (production-readiness boundary).
- Agent: exposes `search_project`; instructions present.

## Risks / open questions

- **Multi-language AST in .NET (highest risk).** Aperant used tree-sitter WASM for
  .ts/.tsx/.py/.js. Scout is .NET: **Roslyn** covers C# natively; other languages
  (TS, Python, etc.) need tree-sitter .NET bindings (maturity varies) or a line-based
  fallback. Decide per target language — **research item for the plan**. Likely v1:
  Roslyn for C#, line-fallback elsewhere; add tree-sitter languages incrementally.
- **Knowledge-graph + incremental + file-watch complexity.** This is the heaviest part
  (closure-table maintenance, staleness, debounce, running safely in the worker).
  Implementation will be **phased**; foundation first, then graph, then watcher.
- **.NET local vector store package** (SQLite-vec / `Microsoft.Extensions.VectorData`
  SQLite): confirm package + stability — research item.
- **Function-calling reliability**: relies on the OpenAI-compat `/v1` connector +
  tool-capable model (`qwen2.5`), already established for the GitHub plugin.

## Evolution (future specs)

- Agent cross-session memory (Feature B) on the same foundation.
- Hybrid retrieval (BM25 fusion + cross-encoder rerank) — Aperant-grade quality.
- More AST languages via tree-sitter; multi-agent orchestration (agent-router).
