---
title: "Testing"
version: "1.0"
date: 2026-10-21
changes: "Founded from the code on 2026-10-21"
page_type: convention
status: active
description: "How Scout is tested: xUnit with Shouldly and NSubstitute, one test project per source project, warnings as errors, and the CI steps that run them."
source: ["Directory.Build.props", "Directory.Packages.props", ".github/workflows/ci.yml", ".github/workflows/ui-ci.yml", "tests/", "ui/package.json", "scripts/check-diagnostics.sh"]
reliability: high
---

# Testing

> Test behaviour at the project boundary with fakes and substitutes; the build fails on any warning; CI runs build then test on every push and pull request to `main`.

## Stack

Versions come from `Directory.Packages.props` (central package management).

- xUnit 2.9.3 (`Directory.Packages.props:52`) with `xunit.runner.visualstudio` 3.1.5 and `Microsoft.NET.Test.Sdk` 18.6.0 (`:53`, `:54`).
- Shouldly 4.3.0 for assertions (`Directory.Packages.props:57`). FluentAssertions was removed because v8 went commercial; do not mix two assertion libraries (comment at `:55`).
- NSubstitute 5.3.0 for substitutes (`Directory.Packages.props:58`).
- `Microsoft.Extensions.TimeProvider.Testing` 10.6.0 for a fake clock (`Directory.Packages.props:59`); the adapter and conversation tests use it.
- `coverlet.collector` 10.0.1 for coverage (`Directory.Packages.props:60`).
- `Grpc.Net.Client` for the control-plane integration tests (`tests/Scout.ControlPlane.Tests/Scout.ControlPlane.Tests.csproj`).
- `Microsoft.AspNetCore.Mvc.Testing` is declared (`Directory.Packages.props:19`) but no test project references it.

## What the build enforces

`Directory.Build.props` applies to every project, test projects included.

- `TreatWarningsAsErrors` true (`Directory.Build.props:7`): any compiler or analyzer warning fails the build.
- `AnalysisLevel` latest (`Directory.Build.props:8`) and `EnforceCodeStyleInBuild` true (`:9`): code-style rules run in the build.
- `Nullable` enabled and target `net10.0` (`Directory.Build.props:5`, `:3`).
- XML documentation is generated and `CS1591` suppressed for non-test projects only (`Directory.Build.props:10`, `:11`). Test projects set `IsTestProject` (for example `tests/Scout.Llm.Tests/Scout.Llm.Tests.csproj:5`), so they generate no documentation file.
- Because test projects emit no documentation file, unused-using diagnostics (IDE0005) are not raised by the plain build. `scripts/check-diagnostics.sh:1` forces them on and fails on any remaining diagnostic; run it on a single project for speed.

## Layout and counts

One test project per source project, named `<Project>.Tests`, except the control-plane trio, which share one. See [[pages/architecture/project-map]]. Counts are `[Fact]` and `[Theory]` attributes, not executed cases (a theory runs once per data row). Total 631 in the ten projects of `Scout.slnx`.

| Test project | Attributes | Covers |
|---|---|---|
| `Scout.Llm.Tests` | 159 | [[pages/services/llm]] |
| `Scout.Knowledge.Tests` | 103 | [[pages/services/knowledge]] |
| `Scout.Conversation.Tests` | 74 | [[pages/services/conversation]] |
| `Scout.ControlPlane.Tests` | 59 | [[pages/services/control-plane]], [[pages/services/control-plane-contracts]], [[pages/services/host-control-plane]] |
| `Scout.Adapter.Slack.Tests` | 54 | [[pages/services/adapter-slack]] |
| `Scout.Adapter.Teams.Tests` | 50 | [[pages/services/adapter-teams]] |
| `Scout.Plugin.GitHub.Tests` | 47 | [[pages/services/plugin-github]] |
| `Scout.Adapter.Discord.Tests` | 34 | [[pages/services/adapter-discord]] |
| `Scout.Skills.Tests` | 31 | [[pages/services/skills]] |
| `Scout.Host.Console.Tests` | 20 | [[pages/services/host-console]] |

`tests/old` holds 653 further attributes for the retired code under `src/old`. It is not in `Scout.slnx`, so CI does not build or run it. Exclude it from any count.

## Patterns in use

- **Composition tests.** Build a `ServiceCollection` from an in-memory configuration and resolve the graph. `tests/Scout.Host.Console.Tests/HostCompositionSmokeTests.cs:115` builds the full host with a handler that throws on any network call, which proves resolution does no I/O.
- **Self-gating tests.** Each optional module has a test for "not configured means not registered" (for example `tests/Scout.Plugin.GitHub.Tests/GitHubServiceCollectionExtensionsTests.cs:24`).
- **Boundary fakes.** SDK edges are faked: a fake HTTP handler for GitHub (`tests/Scout.Plugin.GitHub.Tests/Fakes/`), a recording sender for Teams (`tests/Scout.Adapter.Teams.Tests/Streaming/RecordingSender.cs`), a recording sink for the conversation core (`tests/Scout.Conversation.Tests/Fakes/RecordingResponseSink.cs`), a fake embedder for skills (`tests/Scout.Skills.Tests/FakeEmbeddingGenerator.cs`).
- **Regression tests named for the bug.** `tests/Scout.Llm.Tests/CompositionCycleRegressionTests.cs` pins the kernel and embedder construction cycle.
- **Real integration.** `tests/Scout.ControlPlane.Tests/Integration/ControlPlaneUdsIntegrationTests.cs` starts the control-plane host on a real Unix socket; the `uds` collection (`tests/Scout.ControlPlane.Tests/Integration/UdsCollection.cs:10`) serialises those tests. SQLite tests use a real file (`tests/Scout.Knowledge.Tests/SqliteProjectKnowledgeStoreTests.cs`).
- **No live services.** No test calls Discord, Slack, Teams, GitHub, Azure or an LLM.

## Running

- All tests: `dotnet test` from the repository root (uses `Scout.slnx`).
- One project: `dotnet test tests/Scout.Llm.Tests`.
- One test: `dotnet test tests/Scout.Llm.Tests --filter "FullyQualifiedName~Unknown_Provider"`.
- Pinned SDK 10.0.100 with `latestMinor` roll-forward (`global.json`).

## CI

`.github/workflows/ci.yml`, triggered on push and pull request to `main`:

1. Hook and vault self-tests, then `scripts/check-vault-refs.sh` (`.github/workflows/ci.yml:17` to `:26`), which resolves every `path:line` citation in `vault/pages`.
2. Setup .NET from `global.json`, then `dotnet restore` (`.github/workflows/ci.yml:33`).
3. `dotnet build --configuration Release --no-restore` (`.github/workflows/ci.yml:45`).
4. `dotnet test --configuration Release --no-build` with the trx logger and XPlat coverage (`.github/workflows/ci.yml:48`).

The format-check step is commented out because `dotnet format --verify-no-changes` times out on SDK 10.0.106; style is enforced by the build properties above (`.github/workflows/ci.yml:36` to `:43`).

## UI tests

`.github/workflows/ui-ci.yml` runs on three operating systems: `bun install --frozen-lockfile`, lint, typecheck, `bun run test`, publish the worker binary, package the app (`.github/workflows/ui-ci.yml:69` to `:96`).

- `ui/package.json:19` runs `bun test` in `packages/shared`, `packages/main` and `packages/preload`, and `vitest run` in `packages/renderer`.
- 58 spec or test files: 29 in `ui/packages/main/test`, 3 in `ui/packages/shared/test`, 1 in `ui/packages/preload/test`, 25 in `ui/packages/renderer/src/app`.
- `ui/e2e` is a stub: its `test`, `build`, `lint` and `typecheck` scripts print a no-op message (`ui/e2e/package.json`). There is no end-to-end test yet.
- The path filters in `ui-ci.yml:8` and `:15` name `src/Scout.Host.Worker`, which now lives under `src/old`. A change to `src/Scout.Host.Console` alone does not trigger the UI workflow.

## Related

- [[pages/invariants/no-executive-writes]] and [[pages/invariants/secrets-never-in-source]] list the tests that are missing for each rule.
