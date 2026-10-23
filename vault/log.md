## [2026-10-22] fix | Discord gateway handler no longer awaits the conversation
The MessageReceived handler filters, starts the conversation and returns, so a streaming reply stops blocking the Discord gateway task; StopAsync waits up to 10 s for in-flight replies. Pages updated: services/adapter-discord, new history/adapter-discord, the moved citation in invariants/secrets-never-in-source.
## [2026-10-22] fix | Skill index retried after a failed build; log view keeps a stack at its header's level
SkillSelector no longer caches a failed index build until restart: it retries after one minute on an injected TimeProvider and still caches a success. The desktop Logs screen gives indented, blank and "--- End of" lines the level of the last header from the same stream, so filtering by error keeps the stack. Pages updated: services/skills, new history/skills.
## [2026-10-22] fix | Cloud key takes effect on save; request log names the real provider
Owner testing showed a key saved 27 s after the worker started, so the worker stayed on Ollama, and the log line printed Ollama fields regardless of provider. Saving now restarts a running worker; ChatGateway's log label describes the active provider. Pages updated: services/llm, invariants/pluggable-boundaries, followups.
## [2026-10-22] fix | A provider without credit no longer shows "(connection error)"
Streaming 429s arrive as the OpenAI SDK's ClientResultException, which ChatGateway never caught. One helper now maps quota/billing 429s to the new LlmQuotaExceededException and throttling 429s to LlmRateLimitException; StreamPump tells the user the account has no credit; the Discord and Slack failure footer no longer claims a connection error. Pages updated: services/llm, new history/llm, conventions/testing (Rule I through the real client), shifted citations in services/conversation, invariants/pluggable-boundaries, invariants/no-executive-writes, architecture/restructure-architecture.

## [2026-10-22] feature | OpenAI and Gemini cards take an API key; ChatGPT Plus marked unsupported
ADR 0015. The key goes to the OS keychain as llm.api-key, the cloud model to its own key llm.cloud-model so a local download cannot overwrite it, and build-env maps them per provider at worker start. Pages updated: secrets invariant, desktop-ui, ADR 0007 note, followups, index.

## [2026-10-22] feature | Control-plane client phase 1: Electron launches the host and calls it over gRPC/UDS
`ui/packages/main/src/control-plane/` adds a launcher (per-session token, explicit socket, socket-directory ownership check) and a `@grpc/grpc-js` client; `ListProviders` round-trips against the real `Scout.Host.ControlPlane` in `bun test`, about 0.7 s. `ui-ci.yml` builds the host before the UI tests and its path filters now name the current projects. Windows refuses on both sides until a named-pipe decision. Pages updated: host-control-plane (Consumers), desktop-ui, roadmap/control-plane-client, followups, testing.
## [2026-10-22] query | Why the ChatGPT Plus source card does not connect
It is a disabled placeholder from ADR 0007, like the other four cloud cards; the subscription carries no API access. Recorded in roadmap/followups with the path to wire the API-key cards through the existing Scout.Llm factories.

## [2026-10-22] lint | Full wiki pass, then the vault made true to main
Health check on main 5fb2a15: build, 766 .NET tests, 261 UI tests and both CI workflows green; 80 pages, 0 broken links, 0 orphans, 0 schema errors, 289 citations resolve; report in archive/lint-2026-10-22.md. Findings fixed on branch docs/vault-truth: root CLAUDE.md no longer claims the UI talks gRPC (it spawns the console worker; the control plane has no client), restructure backlog re-verified, domain/cloud-status and domain/code-qa archived as src/old records, new services/desktop-ui written from the code, new roadmap/control-plane-client initiative (owner decision: the gRPC control plane stays and the Electron client gets built), dotnet format follow-up replaces the deleted GitHub issue. Six stale GitHub issues deleted by the owner. Open soft finding: 20 index descriptions over 150 chars.

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
