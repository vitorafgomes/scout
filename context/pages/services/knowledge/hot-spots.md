---
title: "Knowledge: Hot spots"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Knowledge service page."
page_type: service
status: active
description: "Read when a change touches: Hot spots."
source: ["src/Scout.Knowledge/", "tests/Scout.Knowledge.Tests/"]
reliability: high
---

## Hot spots

- `src/Scout.Knowledge/ProjectFileFilter.cs:50`: the only allow-list for what enters the index: the source of the 24 languages the bundled skills cover, the manifests (`*.csproj`, `go.mod` by extension, `Cargo.toml`, `pom.xml`, `build.gradle`), the extensionless build files (`Dockerfile`, `Makefile`, `Jenkinsfile`, `Gemfile`, `Rakefile`, `Procfile`, `CMakeLists.txt`), `.md`, `.txt`, `.rst` and `.adoc` as docs; skipped: `bin`, `obj`, `.git`, `node_modules`, `dist`, `.vs`, `vendor`, `.idea`, `__pycache__`, `.venv`, `venv`, and `target/` at the repository root only (a nested `target/` or `build/` is as likely source as output: Scout's own `ui/build/` holds the packaging configuration). A committed `.env.example`, `.env.sample` or `.env.template` is indexed; `.env` and `.env.<stage>` are not. Anything that is not C# is chunked by a 100-line window.
- `src/Scout.Knowledge/ProjectExpertAgentFactory.cs:88`: OpenAI-typed execution settings in a provider-neutral project.
- `src/Scout.Knowledge/ProjectWatchService.cs:20`: debounce and re-index on file events, the window timed by a `TimeProvider` (`:64`) so the burst test closes it from a fake clock (`tests/Scout.Knowledge.Tests/ProjectWatchServiceTests.cs:217`) instead of waiting out a real one; the tests on that clock await a signal the clock gives each time the service arms a timer (`:249`), never a real-time deadline.
- Since 2026-11-08 the vector store runs on `CommunityToolkit.VectorData.SqliteVec` 1.0.4-preview, the renamed successor of the deprecated Semantic Kernel SqliteVec connector, which no longer holds the Semantic Kernel family back ([[pages/roadmap/followups]]). A database the old connector wrote, checked in as a fixture, opens, searches with the same ranking and takes new rows (`tests/Scout.Knowledge.Tests/OldConnectorDatabaseTests.cs:37`, `:60`).
