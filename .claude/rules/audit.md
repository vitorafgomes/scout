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
- [[pages/decisions/0061-the-pull-request-review-job-as-built|ADR 0061 - The pull request review job as built]] - the review batch turn, the run, its bounds of running time and its one card
- [[pages/decisions/0062-the-review-job-runs-to-its-end-in-parallel-from-the-bell|ADR 0062 - The review job runs to its end, in parallel, shown and controlled from the bell]] - the review read to its end, code first, in waves; the owner's controls; the pause at 60 minutes
- [[pages/decisions/0064-the-review-batch-is-handed-the-purpose-the-rules-and-what-was-removed|ADR 0064 - The review batch is handed the purpose, the project's rules and what was removed]] - the three inputs computed by code, their bounds and how they are kept with the job
- [[pages/decisions/0066-every-finding-carries-evidence-and-its-line-is-derived-by-code|ADR 0066 - Every finding carries evidence, and its line is derived by code]] - the extra request and the placed findings
- [[pages/decisions/0069-the-second-reading-tries-to-refute-and-holds-only-with-a-scenario|ADR 0069 - The second reading tries to refute]] - the second reading's turn, groups and bounds
- [[pages/decisions/0073-the-numbers-of-each-model-turn-are-kept|ADR 0073 - The numbers of each model turn are kept]] - `audit_turn`, the card's counts, the opt-in record
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
| Review batch turn; Review run; Review card | `context/pages/services/audit/how-it-works-review-job.md` |
| Review inputs; the purpose; the rule files; what was removed | `context/pages/services/audit/how-it-works-review-inputs.md` |
| Pause; Continue from Scout Desktop; Stop and status | `context/pages/services/audit/how-it-works-pause.md` |
| Invariants it preserves | `context/pages/services/audit/invariants-it-preserves.md` |
| Tests | `context/pages/services/audit/tests.md` |
| Tests of the review job | `context/pages/services/audit/tests-review-job.md` |
| Evidence; parts; focused readings; the second reading; the turn table; the opt-in record | `context/pages/services/audit/public-contract-review-quality.md` |
| Open items | `context/pages/services/audit/open-items.md` |

For where a type lives or who calls it, ask the code graph (`graphify explain "<Type>"`, `graphify affected "<Type>"`).
