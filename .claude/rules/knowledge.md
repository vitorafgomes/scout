---
paths:
  - "src/Scout.Knowledge/**"
  - "tests/Scout.Knowledge.Tests/**"
---

# Knowledge

Owns discovery, chunking, indexing, the vector store, the optional file watcher, and the Project Expert agent persona. Does not own the embedding model ([[pages/services/llm]]) or the chat flow ([[pages/services/conversation]]).

## Rules this project must keep

- [[pages/invariants/no-executive-writes]]: the three tools read the index; the only write is Scout's own database.
- [[pages/invariants/secrets-never-in-source]]: the filter indexes `.json`, `.yml`, `.yaml`, `.toml` and `.xml` among the configuration formats (`src/Scout.Knowledge/ProjectFileFilter.cs:50`), so a secret written into a committed configuration file is embedded and searchable; since 2026-11-06 it refuses the files that hold credentials by convention, `.env*`, `secrets.json`, `.pem`, `.key`, `.pfx`, `.p12`, `.jks`, `.keystore`, `.crt`, `.cer` (`:83`, `tests/Scout.Knowledge.Tests/ProjectFileFilterTests.cs:55`).
- [[pages/invariants/pluggable-boundaries]]: the store sits behind `IProjectKnowledgeStore`; `Microsoft.Data.Sqlite` and the `CommunityToolkit.VectorData.SqliteVec` connector stay inside this project.

## Pages that govern this project

Read one when your change touches what its title names.

- [[pages/invariants/no-executive-writes|Scout performs descriptive writes only]]
- [[pages/invariants/secrets-never-in-source|Secrets never live in source]]
- [[pages/invariants/pluggable-boundaries|Adapters, LLM providers and plugins are swapped by configuration]]
- [[pages/services/llm|Llm]]
- [[pages/conventions/testing|Testing]]

## What to read for a change

Find the row your change matches and read that page. Read one or two rows, not the table.

| If the change touches | Read |
|---|---|
| Entry point; Abstractions; Model tools; Store; Without embeddings; Chunks and ids; Failures while indexing | `context/pages/services/knowledge/public-contract-entry-point.md` |
| Freshness; Agent; Configuration keys; External boundaries | `context/pages/services/knowledge/public-contract-freshness.md` |
| How it works | `context/pages/services/knowledge/how-it-works.md` |
| Hot spots | `context/pages/services/knowledge/hot-spots.md` |
| Testing footprint | `context/pages/services/knowledge/testing-footprint.md` |

For where a type lives or who calls it, ask the code graph (`graphify explain "<Type>"`, `graphify affected "<Type>"`).
