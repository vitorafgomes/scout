## [2026-10-21] integrate | Follow-ups
Added pages/roadmap/followups.md to index.md under a new Roadmap section, with backlinks from pages/services/knowledge.md (hot spots) and pages/conventions/testing.md (ui-ci).

## [2026-10-21] crystallize | patch/minor dependency bump
NuGet and ui/ packages raised to the latest patch/minor; Semantic Kernel held at 1.78.0 because 1.79+ needs VectorData 10.5.2, which breaks SqliteVec 1.74.0-preview. Created pages/roadmap/followups.md with that hold, the pre-existing prettier drift in ui/ (format is not in ui-ci.yml) and the squirrel-windows peer mismatch.
## [2026-10-21] crystallize | ui prettier check in CI
Formatted the 26 ui files that drifted from prettier 3.9.10 and added a format step to ui-ci.yml; testing.md records the new step.
## [2026-10-21] integrate | Adapter Slack history
Added pages/history/adapter-slack.md to index.md under a new History section; pages/services/adapter-slack.md links it.

## [2026-10-21] crystallize | major dependency bump
gRPC 2.84, Protobuf 3.36, NSubstitute 6, xunit runner 4, SQLitePCLRaw 3, Roslyn 5.9, SlackNet 0.19 (StopAsync awaits a token-bounded DisconnectAsync, DisposeAsync always disposes); ui on Angular 22, Electron 45, vitest 5, jsdom 30, js-yaml 5, renderer TypeScript 6.0; TypeScript 7 deferred on typescript-eslint and Angular peer ranges; ui-ci pins Node 24 for Angular 22.

## [2026-10-21] founded | harness and vault brought over from Smart.Management
Root CLAUDE.md (rules A to J, A = read-only), AGENTS.md, .claude/ (scout-pipeline skill with four lanes and four evals, agents scout-implementer/mechanic/reviewer/optimizer, seven sh+jq hooks with five self-tests, scout-review workflow, stryker-project skill, wiki-* skills), scripts/check-diagnostics.sh and check-vault-refs.sh, CI hook self-test step, .gitattributes for this log. Vault: 14 ADRs (0001-0014) and the technical architecture, domain, runbook and overview notes migrated verbatim from the owner's Obsidian folder (two long pages split; two config values redacted; plans, handoffs, sessions and the security audit deliberately left private); 12 service pages, 4 invariant pages, testing.md, tooling.md and project-map written from the code with 287 verified citations. graphify git hooks installed, .graphifyignore excludes the src/old and tests/old archive, first AST graph built locally (6655 nodes). Not brought: Jev, the integration-database probe, the comment sweeper. Owner decisions: working model as Smart.Management, no Jev, branch + PR delivery, never an AI attribution line. Findings for the first review are in the index coverage note; a credential finding was reported to the owner directly.
