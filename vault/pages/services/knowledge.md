---
title: "Knowledge"
version: "1.2"
date: 2026-10-21
updated: 2026-11-06
changes: "v1.2 (2026-11-06) the file filter covers every language the skills cover, the project manifests and the extensionless build files, and never a credentials file. v1.1 added the hot spot that holds Semantic Kernel at 1.78.0"
page_type: service
status: active
description: "Indexes configured repositories into a local SQLite vector store and answers search_project, find_symbol and list_projects tool calls from it, through a Project Expert chat agent."
source: ["src/Scout.Knowledge/", "tests/Scout.Knowledge.Tests/"]
reliability: high
---

# Knowledge

> Reads configured repositories, embeds them, and lets the model search them; never writes to them.

## To change here, read

- [[pages/invariants/no-executive-writes]]
- [[pages/invariants/secrets-never-in-source]]
- [[pages/invariants/pluggable-boundaries]]
- [[pages/services/llm]]
- [[pages/conventions/testing]]

## Responsibility

Owns discovery, chunking, indexing, the vector store, the optional file watcher, and the Project Expert agent persona. Does not own the embedding model ([[pages/services/llm]]) or the chat flow ([[pages/services/conversation]]).

## Public contract

- **Entry point:** `AddProjectKnowledge(IServiceCollection, IConfiguration)` at `src/Scout.Knowledge/KnowledgeServiceCollectionExtensions.cs:30`.
- **Abstractions:** `IProjectKnowledgeStore` (`src/Scout.Knowledge/IProjectKnowledgeStore.cs:4`) implemented by `SqliteProjectKnowledgeStore` (`src/Scout.Knowledge/SqliteProjectKnowledgeStore.cs:7`); `IChunker` (`src/Scout.Knowledge/IChunker.cs:4`) implemented by `FileChunker`, a Roslyn chunker for C# with a line-window fallback (`src/Scout.Knowledge/FileChunker.cs:7`); `ProjectIndexer.IndexAsync` (`src/Scout.Knowledge/ProjectIndexer.cs:16`); `ProjectKnowledgeService` (`src/Scout.Knowledge/ProjectKnowledgeService.cs:16`); `ProjectResolutionService` (`src/Scout.Knowledge/ProjectResolutionService.cs:13`); `ProjectDiscoveryService` (`src/Scout.Knowledge/ProjectDiscoveryService.cs:12`).
- **Model tools:** plugin `ProjectKnowledge` with `search_project`, `find_symbol`, `list_projects` (`src/Scout.Knowledge/ProjectKnowledgePlugin.cs:26,41,53`), registered only when a project or root is configured (`src/Scout.Knowledge/KnowledgeServiceCollectionExtensions.cs:72`).
- **Agent:** `ProjectExpertAgentFactory.Create` (`src/Scout.Knowledge/ProjectExpertAgentFactory.cs:57`).
- **Configuration keys:** `Scout:Projects` with `Items` (`Name`, `RepoPath`), `Roots`, and `Watch` (`Enabled`, `DebounceMilliseconds`) (`src/Scout.Knowledge/ProjectsOptions.cs:7`).
- **External boundaries:** filesystem read of repository files (`src/Scout.Knowledge/ProjectIndexer.cs:63`) and a `FileSystemWatcher` when enabled (`src/Scout.Knowledge/ProjectWatchService.cs:84`); filesystem write of `project-knowledge.db` under the user's local application data, folder `Scout` (`src/Scout.Knowledge/KnowledgeServiceCollectionExtensions.cs:100`); embedding calls through `IEmbeddingGenerator`.

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

Indexing runs from the host command `index <project>` ([[pages/services/host-console]]) or from the watcher. The agent and the plugin are skipped when no project is configured, so the model never sees a tool that would return nothing (`src/Scout.Knowledge/KnowledgeServiceCollectionExtensions.cs:64`). The watcher is registered only when enabled and at least one item exists (`:92`). Option validation warns and never throws (`src/Scout.Knowledge/ProjectsOptionsValidator.cs:38`).

## Invariants

- [[pages/invariants/no-executive-writes]]: the three tools read the index; the only write is Scout's own database.
- [[pages/invariants/secrets-never-in-source]]: the filter indexes `.json`, `.yml`, `.yaml`, `.toml` and `.xml` among the configuration formats (`src/Scout.Knowledge/ProjectFileFilter.cs:50`), so a secret written into a committed configuration file is embedded and searchable; since 2026-11-06 it refuses the files that hold credentials by convention, `.env*`, `secrets.json`, `.pem`, `.key`, `.pfx`, `.p12`, `.jks`, `.keystore`, `.crt`, `.cer` (`:83`, `tests/Scout.Knowledge.Tests/ProjectFileFilterTests.cs:55`).
- [[pages/invariants/pluggable-boundaries]]: the store sits behind `IProjectKnowledgeStore`.

## Hot spots

- `src/Scout.Knowledge/ProjectFileFilter.cs:50`: the only allow-list for what enters the index: the source of the 24 languages the bundled skills cover, the manifests (`*.csproj`, `go.mod` by extension, `Cargo.toml`, `pom.xml`, `build.gradle`), the extensionless build files (`Dockerfile`, `Makefile`, `Jenkinsfile`, `Gemfile`, `Rakefile`, `Procfile`, `CMakeLists.txt`), `.md`, `.txt`, `.rst` and `.adoc` as docs; skipped: `bin`, `obj`, `.git`, `node_modules`, `dist`, `.vs`, `vendor`, `.idea`, `__pycache__`, `.venv`, `venv`, and `target/` at the repository root only (a nested `target/` or `build/` is as likely source as output: Scout's own `ui/build/` holds the packaging configuration). A committed `.env.example`, `.env.sample` or `.env.template` is indexed; `.env` and `.env.<stage>` are not. Anything that is not C# is chunked by a 100-line window.
- `src/Scout.Knowledge/ProjectExpertAgentFactory.cs:82`: OpenAI-typed execution settings in a provider-neutral project.
- `src/Scout.Knowledge/ProjectWatchService.cs:19`: debounce and re-index on file events.
- The SqliteVec connector holds the whole Semantic Kernel family at 1.78.0 ([[pages/roadmap/followups]]).

## Testing footprint

`tests/Scout.Knowledge.Tests` (107 test attributes, 14 files): chunker, filter, indexer, SQLite store against a real file, watcher, option validation, discovery, resolution, plugin output, agent persona, DI gating (`KnowledgeServiceCollectionExtensions` tests), and a retrieval acceptance test (`DeadLetterRetrievalAcceptanceTests.cs`).

## Related

Direct project references: none. Referenced by [[pages/services/host-console]]. Map: [[pages/architecture/project-map]].
