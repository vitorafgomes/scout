---
paths:
  - "src/Scout.Audit/**"
  - "tests/Scout.Audit.Tests/**"
  - "tests/Scout.ArchTests/PersistenceTests.cs"
  - "src/Scout.Host.Console/HostComposition.cs"
  - "src/Scout.Host.Console/ReviewEnvironmentAdapter.cs"
  - "tests/Scout.Host.Console.Tests/HostAuditTurnTests.cs"
---

# Audit

Owns the audit job: the plan of batches, the `audit.db` file, its three tables and their migrations, the batch turn's instructions and the functions it offers, the merge of findings across batches, the posts into the originating conversation, and the start, pause, stop and status sentences. Does not own the snapshot or the reads (the GitHub plugin, [[pages/services/plugin-github]]), the project search ([[pages/services/knowledge]]), the audit text itself (`code-audit`, [[pages/services/skills]]), the cards and the issues asked from them (the desktop and `create_issue`, each issue queued for the owner's approval since 2026-11-09, [[pages/decisions/0032-per-feature-write-permissions]]), nor the chat functions that call it (the GitHub plugin declares them; see Open items).

## Pages that govern this project

Read one when your change touches what its title names.

- [[pages/decisions/0042-audit-as-a-job-with-saved-progress|ADR 0042 - The audit as a job with saved progress]] - the design this project implements
- [[pages/decisions/0052-persistence-behind-stores-on-entity-framework|ADR 0052 - Persistence behind one store per use case, on Entity Framework Core with SQLite]] - the store interface and Entity Framework behind it
- [[pages/decisions/0055-pull-request-review-as-a-job-in-batches|ADR 0055 - The pull request review as a job in batches]] - the pull request review as a job of this engine, its kind and pull request on the job row
- [[pages/decisions/0039-repository-snapshot-under-the-data-folder|ADR 0039 - A repository snapshot under Scout's data folder, read locally and searched by the Knowledge index]] - the snapshot the plan is cut from
- [[pages/decisions/0040-review-findings-as-cards-in-chat|ADR 0040 - The review's findings as cards in the chat, posted as one review from the cards]] - the `scout-findings` block and `present_audit_findings`
- [[pages/decisions/0033-channel-operator-architecture|ADR 0033 - Channel operator architecture: state in the control plane, pipeline in Scout.Operator, media through the connected AI Source, progress back to the originating channel]] - the background-job and origin-post precedent
- [[pages/decisions/0032-per-feature-write-permissions|ADR 0032 - Features declare what they write, and the owner grants each one]]
- [[pages/invariants/no-executive-writes|Scout performs descriptive writes only]]
- [[pages/invariants/pluggable-boundaries|Adapters, LLM providers and plugins are swapped by configuration]]
- [[pages/invariants/secrets-never-in-source|Secrets never live in source]]
- [[pages/invariants/local-first-default|Ollama is the default LLM provider]]
- [[pages/services/plugin-github|Plugin.GitHub]], [[pages/services/knowledge|Knowledge]], [[pages/services/skills|Skills]], [[pages/services/conversation|Conversation]]

## What to read for a change

Find the row your change matches and read that page. Read one or two rows, not the table.

| If the change touches | Read |
|---|---|
| Public contract | `context/pages/services/audit/public-contract.md` |
| mermaid; Start; Review start; Checks before a review | `context/pages/services/audit/how-it-works-mermaid.md` |
| Plan; Rows; Merge | `context/pages/services/audit/how-it-works-plan.md` |
| Batch turn; Worker | `context/pages/services/audit/how-it-works-batch-turn.md` |
| Pause; Continue from Scout Desktop; Stop and status | `context/pages/services/audit/how-it-works-pause.md` |
| Invariants it preserves | `context/pages/services/audit/invariants-it-preserves.md` |
| Tests | `context/pages/services/audit/tests.md` |
| Open items | `context/pages/services/audit/open-items.md` |

For where a type lives or who calls it, ask the code graph (`graphify explain "<Type>"`, `graphify affected "<Type>"`).
