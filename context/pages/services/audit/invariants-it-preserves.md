---
title: "Audit: Invariants it preserves"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Audit service page."
page_type: service
status: active
description: "Read when a change touches: Invariants it preserves."
source: ["src/Scout.Audit/", "tests/Scout.Audit.Tests/", "tests/Scout.ArchTests/PersistenceTests.cs", "context/pages/decisions/0052-persistence-behind-stores-on-entity-framework.md", "src/Scout.Host.Console/HostComposition.cs", "src/Scout.Host.Console/ReviewEnvironmentAdapter.cs", "tests/Scout.Host.Console.Tests/HostAuditTurnTests.cs", "context/pages/decisions/0042-audit-as-a-job-with-saved-progress.md"]
reliability: high
---

## Invariants it preserves

- **Declared writes.** The only write is under Scout's data folder: the snapshot goes through the gate as `snapshot_repository`; `audit.db` is written by the job the owner's grant started. The checks before a review make no call that leaves the process: they read a configuration value, the start's arguments and the job's rows, and a block asks the gate for nothing (`tests/Scout.Audit.Tests/RepositoryReviewsTests.cs:460`). A review start writes only `audit.db` rows and no function reaches it yet; its snapshot asks the gate like an audit's, and a refused one starts nothing (`tests/Scout.Audit.Tests/RepositoryReviewsTests.cs:98`). The job never calls a GitHub write; issues are still asked only from the cards, and each waits for the owner's approval. `tests/Scout.Audit.Tests/AuditBatchRunnerTests.cs:44` pins the six offered functions; `tests/Scout.Host.Console.Tests/HostAuditTurnTests.cs:88` pins the two gate requests.
- **Pluggable boundaries.** The model is reached through Semantic Kernel only; the project references no plugin, adapter, host or control-plane assembly (`tests/Scout.ArchTests/ArchitectureTests.cs:126`), and a review's patches arrive as arguments; only `SqliteAuditStore` and its `Storage` namespace reference SQLite or Entity Framework, and no other file holds SQL (`tests/Scout.ArchTests/PersistenceTests.cs:66`, `:79`); no SDK exception type is classified or named here, because `LlmFailures` translates it inside Scout.Llm.
- **Secrets.** No token is read, stored or logged: the rows hold paths, titles and reasons in words; logs name the job id and the exception type.
- **Local first.** Works on Ollama: the batch turn uses the kernel's chat service with automatic function calling through the base Semantic Kernel settings, the same path every provider takes; `tests/Scout.Host.Console.Tests/HostAuditTurnTests.cs:111` runs a batch on the Ollama connector against a fake server, reading the snapshot with the real GitHub plugin and parsing the block the real `present_audit_findings` attaches, and pins the six tools the request offers.
