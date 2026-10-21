---
title: "`Plugin.CodeQA`"
version: "1.0"
date: 2026-04-21
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: knowledge
status: stub
description: "Read-only code Q&A plugin. Answers natural-language questions about a configured GitHub repository using retrieval-augmented generation (RAG)."
source:
  - "obsidian-vault/scout/03-domain/code-qa.md"
reliability: medium
---

# `Plugin.CodeQA`

Read-only code Q&A plugin. Answers natural-language questions about a
configured GitHub repository using retrieval-augmented generation (RAG).

## Responsibility

- Index one configured GitHub repository: source files, READMEs, architecture
  docs.
- On query, retrieve the most relevant chunks and compose an LLM answer with
  citations (file path + line range).
- Never modifies the repo. Never opens issues or PRs. Never comments.

## Scope (V1)

- **One GitHub repository** per Scout instance, configured statically.
- **Public or private** — supported via a read-only personal access token.
- **GitHub only** — Azure DevOps integration is V2.
- **Text files** only (code, markdown, text). Binary files are skipped.

## Semantic Kernel functions exposed

- `ask_code(question)` — main entry; returns answer + citations.
- `search_code(query, top_k?)` — raw retrieval, no LLM step (useful for debug
  and as a fast path when the caller only wants pointers).
- `list_indexed_files()` — enumerates what's in the index.

All functions declare `Capabilities.ReadOnly = true`.

## Pipeline

```
GitHub repo
    │
    ▼
[ Ingestor ] ── reads files via GitHub REST API (tokenized, read-only)
    │
    ▼
[ Chunker ] ── splits into ~500-token chunks with file-path + line metadata
    │
    ▼
[ Embedder ] ── SK IEmbeddingGenerationService (Ollama nomic-embed-text default)
    │
    ▼
[ Vector store ] ── configurable: in-memory (default), Qdrant, pgvector
    │
    ▼
[ Retriever ] ── on query, top-k by cosine similarity
    │
    ▼
[ LLM composer ] ── SK IChatCompletionService with retrieved context
    │
    ▼
Answer + citations
```

## Domain model

```csharp
public record CodeAnswer(
    string Text,
    IReadOnlyList<Citation> Citations,
    string ModelUsed,
    TimeSpan Latency);

public record Citation(
    string FilePath,
    int StartLine,
    int EndLine,
    double RelevanceScore);

public record CodeChunk(
    string FilePath,
    int StartLine,
    int EndLine,
    string Content,
    IReadOnlyList<float> Embedding);
```

## Ingestion strategy

- **Trigger (V1):** on-demand via a one-off CLI command
  (`scout index --repo <owner>/<name>`). Ran at deploy time.
- **Incremental updates (V1.1):** GitHub webhook or scheduled re-index.
- **Full re-index:** always supported; cheap enough for repos under a
  few hundred MB.

## Read-only enforcement

- The GitHub PAT used by this plugin has **only** `repo:read` scope. CI
  verifies the scope before deployment.
- The plugin's `Capabilities` declares only read scopes.
- No code path in `Plugin.CodeQA` calls any GitHub endpoint with verb
  `POST`, `PATCH`, `PUT`, or `DELETE`. Static analyzer rule in CI enforces this.

## Configuration shape

```json
"Plugins": {
  "CodeQA": {
    "Repository": {
      "Owner": "<redacted>",
      "Name":  "scout",
      "Branch": "main",
      "TokenEnvVar": "SCOUT_GITHUB_TOKEN"
    },
    "Embeddings": {
      "Provider": "Ollama",
      "Model":    "nomic-embed-text"
    },
    "VectorStore": {
      "Provider": "InMemory"
    },
    "Retrieval": {
      "TopK": 6,
      "MaxContextTokens": 4000
    },
    "Ingestion": {
      "IncludeGlobs": [ "**/*.cs", "**/*.md", "**/*.ts" ],
      "ExcludeGlobs": [ "**/bin/**", "**/obj/**", "**/node_modules/**" ],
      "MaxFileBytes": 200000
    }
  }
}
```

## Error handling

- Repo fetch failure → index stays stale, plugin reports "index unavailable"
  with last successful sync timestamp; query still returns best-effort.
- Embedding provider failure → cached embeddings from previous runs are used;
  new files are deferred.
- LLM failure → return raw retrieval results (citations only) with a
  "LLM composition unavailable" flag.
- Query with no relevant matches → explicit "I could not find anything
  relevant in the indexed code" response; never hallucinates.

## Open questions

- Default vector store: in-memory (zero setup, lost on restart) vs. SQLite
  with vss extension (persistent, zero external deps) vs. pgvector (needs
  Postgres). In-memory likely simplest for V1, but persistence matters.
- Hybrid retrieval (embedding + BM25) vs. embeddings only in V1.
- Chunking by AST (Roslyn for C#) vs. by token window. AST is better but
  requires per-language parsers.
- Prompt injection: what happens if the indexed code contains
  "ignore previous instructions"? Need sanitization layer around retrieved
  content before passing to LLM.

These are planning-time decisions, resolved during implementation planning.
