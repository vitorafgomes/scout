---
title: "Knowledge: How it works"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Knowledge service page."
page_type: service
status: active
description: "Read when a change touches: How it works."
source: ["src/Scout.Knowledge/", "tests/Scout.Knowledge.Tests/"]
reliability: high
---

## How it works

```mermaid
flowchart LR
    repo["RepoPath files"] --> flt["ProjectFileFilter"]
    flt --> ch["FileChunker"]
    ch --> idx["ProjectIndexer"]
    idx --> st["SqliteProjectKnowledgeStore"]
    q["search_project"] --> svc["ProjectKnowledgeService"]
    svc --> st
    w["ProjectWatchService (opt-in)"] --> idx
```

Indexing runs from the host command `index <project>` ([[pages/services/host-console]]), from the first use of a project in a tool call, or per file from the watcher. The agent is skipped when no project is configured; the plugin stays, and with nothing registered its functions answer that no project is indexed (`src/Scout.Knowledge/KnowledgeServiceCollectionExtensions.cs:65`). The watcher is registered only when enabled and at least one item exists (`:92`). Option validation warns and never throws (`src/Scout.Knowledge/ProjectsOptionsValidator.cs:38`).
