---
title: "Testing"
version: "1.11"
date: 2026-10-21
updated: 2026-11-05
changes: "Line endings: LF on every checkout for the source-text ui tests, and a relative fixture that stays relative on Windows"
page_type: convention
status: active
description: "How Scout is tested: xUnit with Shouldly and NSubstitute, one test project per source project, warnings as errors, and the CI steps that run them."
source: ["Directory.Build.props", "Directory.Packages.props", ".github/workflows/ci.yml", ".github/workflows/ui-ci.yml", "tests/", "ui/package.json", "scripts/check-diagnostics.sh", "docs/site.test.js"]
reliability: high
---

# Testing

> Test behaviour at the project boundary with fakes and substitutes; the build fails on any warning; CI runs build then test on every push and pull request to `main`.

## Stack

Versions come from `Directory.Packages.props` (central package management).

- xUnit 2.9.3 (`Directory.Packages.props:60`) with `xunit.runner.visualstudio` 4.0.1 (still runs xUnit v1, v2 and v3 test projects) and `Microsoft.NET.Test.Sdk` 18.11.0 (`:61`, `:62`).
- Shouldly 4.3.0 for assertions (`Directory.Packages.props:65`). FluentAssertions was removed because v8 went commercial; do not mix two assertion libraries (comment at `:63`).
- NSubstitute 6.2.0 for substitutes (`Directory.Packages.props:66`).
- `Microsoft.Extensions.TimeProvider.Testing` 10.11.0 for a fake clock (`Directory.Packages.props:67`); the adapter and conversation tests use it.
- `coverlet.collector` 10.1.0 for coverage (`Directory.Packages.props:68`).
- `Grpc.Net.Client` for the control-plane integration tests (`tests/Scout.ControlPlane.Tests/Scout.ControlPlane.Tests.csproj`).
- `Microsoft.AspNetCore.Mvc.Testing` is declared (`Directory.Packages.props:19`) but no test project references it.

## What the build enforces

`Directory.Build.props` applies to every project, test projects included.

- `TreatWarningsAsErrors` true (`Directory.Build.props:7`): any compiler or analyzer warning fails the build.
- `AnalysisLevel` latest (`Directory.Build.props:8`) and `EnforceCodeStyleInBuild` true (`:9`): code-style rules run in the build.
- `Nullable` enabled and target `net10.0` (`Directory.Build.props:5`, `:3`).
- XML documentation is generated and `CS1591` suppressed for non-test projects only (`Directory.Build.props:10`, `:11`). Test projects set `IsTestProject` (for example `tests/Scout.Llm.Tests/Scout.Llm.Tests.csproj:5`), so they generate no documentation file.
- Because test projects emit no documentation file, unused-using diagnostics (IDE0005) are not raised by the plain build. `scripts/check-diagnostics.sh:1` forces them on and fails on any remaining diagnostic; run it on a single project for speed. CI runs it on the whole solution.
- `AnalysisMode` Recommended (`Directory.Build.props:9`) and fifteen rules raised to warning in the root `.editorconfig` (`.editorconfig:43`), since 2026-11-06; the list, the triage and the gate that keeps them on are on [[pages/conventions/tooling]].
- `tests/Scout.ArchTests` (2026-11-06) is the suite about the repository itself: the layering of [[pages/architecture/project-map]] as NetArchTest rules (leaves reference no other Scout assembly, `tests/Scout.ArchTests/ArchitectureTests.cs:55`; adapters and plugins reference neither each other nor a host, `:69`; the conversation core reaches only Llm and Skills, `:103`; Operator reaches the control plane only through its own abstraction, `:113`), the analyzer gate, the test-suite health (no skipped test, `tests/Scout.ArchTests/TestSuiteHealthTests.cs:26`; every source project has `tests/<Name>.Tests` except the two the map covers from `Scout.ControlPlane.Tests`, `:15`; every test project is in `Scout.slnx`; no test directory without a project), and three source scanners: a format specifier in an interpolated string names its culture (`tests/Scout.ArchTests/CultureExplicitFormattingTests.cs:74`), a comment never cites a vault path, an ADR number or an issue number (`tests/Scout.ArchTests/CommentReferenceTests.cs:36`; the vault cites code, code does not cite the vault), and every test project asserts with Shouldly and none references FluentAssertions (`tests/Scout.ArchTests/AssertionLibraryTests.cs:12`). The scanners blank string literals and comments before matching, with their own tests in `SourceTextTests.cs`.

## Layout and counts

One test project per source project, named `<Project>.Tests`, except the control-plane trio, which share one (`TestSuiteHealthTests.EverySourceProjectHasATestProject` enforces it), plus `Scout.ArchTests` over the repository itself. See [[pages/architecture/project-map]]. Counts are `[Fact]` and `[Theory]` attributes, not executed cases (a theory runs once per data row). Total 936 in the fourteen projects of `Scout.slnx` (recounted 2026-11-02).

| Test project | Attributes | Covers |
|---|---|---|
| `Scout.Llm.Tests` | 217 | [[pages/services/llm]] |
| `Scout.Conversation.Tests` | 124 | [[pages/services/conversation]] |
| `Scout.Knowledge.Tests` | 107 | [[pages/services/knowledge]] |
| `Scout.Adapter.Discord.Tests` | 70 | [[pages/services/adapter-discord]] |
| `Scout.ControlPlane.Tests` | 68 | [[pages/services/control-plane]], [[pages/services/control-plane-contracts]], [[pages/services/host-control-plane]] |
| `Scout.Adapter.Slack.Tests` | 59 | [[pages/services/adapter-slack]] |
| `Scout.Plugin.GitHub.Tests` | 55 | [[pages/services/plugin-github]] |
| `Scout.Adapter.Teams.Tests` | 50 | [[pages/services/adapter-teams]] |
| `Scout.Plugin.YouTube.Tests` | 45 | [[pages/services/plugin-youtube]] |
| `Scout.Plugin.Kubernetes.Tests` | 39 | [[pages/services/plugin-kubernetes]] |
| `Scout.Skills.Tests` | 36 | [[pages/services/skills]] |
| `Scout.Host.Console.Tests` | 35 | [[pages/services/host-console]] |
| `Scout.Adapter.Local.Tests` | 16 | [[pages/services/adapter-local]] |
| `Scout.Transcription.Whisper.Tests` | 15 | [[pages/services/transcription-whisper]] |
| `Scout.Meetings.Tests` | 12 | [[pages/services/meetings]] |

`tests/old`, the 653 test attributes of the retired first cut, was deleted with it on 2026-11-06; both are in the history before `93da0d4`.

## Patterns in use

- **Composition tests.** Build a `ServiceCollection` from an in-memory configuration and resolve the graph. `tests/Scout.Host.Console.Tests/HostCompositionSmokeTests.cs:117` builds the full host with a handler that throws on any network call, which proves resolution does no I/O.
- **Self-gating tests.** Each optional module has a test for "not configured means not registered" (for example `tests/Scout.Plugin.GitHub.Tests/GitHubServiceCollectionExtensionsTests.cs:22`).
- **Boundary fakes.** SDK edges are faked: a fake HTTP handler for GitHub (`tests/Scout.Plugin.GitHub.Tests/Fakes/`), a recording sender for Teams (`tests/Scout.Adapter.Teams.Tests/Streaming/RecordingSender.cs`), a recording sink for the conversation core (`tests/Scout.Conversation.Tests/Fakes/RecordingResponseSink.cs`), a fake embedder for skills (`tests/Scout.Skills.Tests/FakeEmbeddingGenerator.cs`).
- **One failing-call test per external boundary (Rule I).** A failing-call test makes the real client library raise its real exception, by placing a fake `HttpMessageHandler` under the real connector, instead of constructing the exception by hand: `tests/Scout.Llm.Tests/ChatGatewayRateLimitTests.cs:151` drives the real OpenAI connector into a 429, which a hand-made `HttpOperationException` had hidden ([[pages/history/llm]]).
- **Stdio boundaries.** The local adapter is tested over a real `AnonymousPipeServerStream` for stdin and a capturing `TextWriter` for stdout (`tests/Scout.Adapter.Local.Tests/LineCapture.cs`), so the line splitting, the 20 MB cap and end of file run through the same stream code as production; its wait helper fails with a Shouldly message that names the missing event and prints the captured stdout, never a bare timeout. On the desktop side the supervisor's stdin is a Node `Writable` whose callback fails with EPIPE (`ui/packages/main/test/worker-supervisor.test.ts`).
- **Regression tests named for the bug.** `tests/Scout.Llm.Tests/CompositionCycleRegressionTests.cs` pins the kernel and embedder construction cycle.
- **Real integration.** `tests/Scout.ControlPlane.Tests/Integration/ControlPlaneUdsIntegrationTests.cs` starts the control-plane host on a real Unix socket; the `uds` collection (`tests/Scout.ControlPlane.Tests/Integration/UdsCollection.cs:10`) serialises those tests. SQLite tests use a real file (`tests/Scout.Knowledge.Tests/SqliteProjectKnowledgeStoreTests.cs`).
- **No live services.** No test calls Discord, Slack, Teams, GitHub, Azure or an LLM.

## Running

- All tests, one project at a time with a verdict per project: `bash scripts/test-per-project.sh`. A solution-wide `dotnet test` from the root still works but reports one total.
- One project: `dotnet test tests/Scout.Llm.Tests`.
- One test: `dotnet test tests/Scout.Llm.Tests --filter "FullyQualifiedName~Unknown_Provider"`.
- Pinned SDK 10.0.100 with `latestMinor` roll-forward (`global.json`).

## CI

`.github/workflows/ci.yml`, triggered on push and pull request to `main`:

1. The seven hook self-tests, the two script self-tests, then `scripts/check-vault-refs.sh` (`.github/workflows/ci.yml:17`), which resolves every `path:line` citation in `vault/pages`.
2. Setup .NET from `global.json`, then `dotnet restore`.
3. A second SDK, 10.0.2xx, which `global.json`'s `latestMinor` roll-forward then selects for the rest of the job, and `dotnet format Scout.slnx --verify-no-changes --no-restore` (`.github/workflows/ci.yml:47`): the 10.0.1xx band cannot open the workspace `dotnet format` needs.
4. One build, through the gate: `scripts/check-diagnostics.sh Scout.slnx info Release` (`.github/workflows/ci.yml:55`) rebuilds the solution in Release with the documentation file and the IDE rules forced on, and fails on any diagnostic at any severity.
5. `scripts/test-per-project.sh --configuration Release --no-build` with the trx logger and XPlat coverage (`.github/workflows/ci.yml:60`), on the gate's output: one verdict per project, Skipped must be 0.

The format check was disabled from 2026-10-21 to 2026-11-06 because `dotnet format --verify-no-changes` times out on the 10.0.1xx SDK; it runs again under 10.0.2xx. Locally, the same SDK from `$HOME/.dotnet` runs it (tooling page); `dotnet format whitespace --folder` is the fallback under the pinned one.

## UI tests

`.github/workflows/ui-ci.yml` runs on three operating systems: `bun install --frozen-lockfile`, lint, typecheck, build the control-plane host, `bun run test`, publish the worker binary, package the app (`.github/workflows/ui-ci.yml:87` to `:132`). The Package step passes `--publish never` (`.github/workflows/ui-ci.yml:130`), as the release workflow does: `ui/build/electron-builder.yml` declares a GitHub publish provider for the updater metadata, and without the flag electron-builder would try to publish on a push to `main` and fail for want of a token. The prettier check (`bun run format`) runs on the Linux leg only, because Windows checkouts convert to CRLF and `ui/.prettierrc.json` requires LF.

- `ui/package.json:19` runs `bun test` in `packages/shared`, `packages/main` and `packages/preload`, and `vitest run` in `packages/renderer`.
- 81 spec or test files: 43 in `ui/packages/main/test`, 4 in `ui/packages/shared/test`, 1 in `ui/packages/preload/test`, 33 in `ui/packages/renderer/src/app` (counted 2026-10-30).
- On GitHub Actions Bun prints every test title. A title that interpolates its data (`%p`, `%o`, `%j`) with a large fixture makes a log line the runner's problem matchers cannot regex-match, and the job hangs until `timeout-minutes` (20, `.github/workflows/ui-ci.yml:34`) ends it. An `it.each` with a large fixture carries a short label as its first column and puts only that label in the title (`ui/packages/main/test/quick-ask.test.ts:384`). Measured 2026-11-02: one 14,000,004-character title stalled all three runners for an hour.
- The project site under `docs/` has one test file, `docs/site.test.js`, run with `bun test ./docs` from the repository root. It covers the one HTTP call (`fetchLatestRelease`, with an injected `fetch`: 403, 404 and a network failure yield `null`) and the pure functions in `docs/site.js`: detecting the visitor's platform, sorting a release's assets per platform and ignoring blockmaps, `latest*.yml` and checksums, the size label, and the model with and without a release. The `Test (site)` step runs it right after `Setup Node` on all three legs, before `bun install` (`.github/workflows/ui-ci.yml:64`); it needs no install, because the module imports nothing. The DOM part of `docs/site.js` has no test. [[pages/decisions/0029-project-site-and-releases]]
- `ui/e2e` is a stub: its `test`, `build`, `lint` and `typecheck` scripts print a no-op message (`ui/e2e/package.json`). There is no end-to-end test yet.
- The path filters (`.github/workflows/ui-ci.yml:8` to `:11` and `:19` to `:22`) name `src/Scout.Host.Console`, `src/Scout.Host.ControlPlane`, `src/Scout.ControlPlane` and `src/Scout.ControlPlane.Contracts`, so a change to the worker or the control plane triggers the UI workflow; `docs/**` (`:14`, `:25`) triggers it for the project site.
- `.gitattributes` checks every text file out with LF on every OS (`* text=auto eol=lf`, `.gitattributes:2`), because five tests in `ui/packages/main/test/` read a source file as text and compare exact strings (`quick-ask.test.ts` on `main.ts` among them); on the Windows runner of `ui-ci.yml` the default checkout turned LF into CRLF and those tests went red with nothing else wrong (the log entry of 2026-11-05 has the dates). A fixture that must be a relative path to a real directory is `path.relative(root, dir)` under a `process.chdir(root)` restored in `finally`, never `path.relative(process.cwd(), dir)` from a temp folder: the Windows runner keeps the temp folder on another drive, so `path.relative` answers an absolute path there, and a relative name that does not exist would pass for the wrong reason (`ui/packages/main/test/operator-media.test.ts:285`).
- The `Build control-plane host` step (`.github/workflows/ui-ci.yml:107`) builds `Scout.Host.ControlPlane` in Release before `Test (UI)`, because `ui/packages/main/test/control-plane-integration.test.ts` launches the real binary and fails, never skips, when it is missing.

- **Catch a rejection before triggering it.** In `bun:test`, a promise that rejects while the test awaits something else is reported as an unhandled rejection and fails the test, even when `expect(...).rejects` is awaited right after; the OAuth loopback tests awaited a `fetch` first and failed on macOS only, where the server rejects before `fetch` resolves. An unawaited `expect(...).rejects` does not help: Bun evaluates it at once and blocks the test until the promise settles. Attach `const settled = promise.catch((err: unknown) => err)` before the call that triggers the rejection, then assert `expect(await settled).toBeInstanceOf(...)` (`ui/packages/main/test/oauth-loopback.test.ts:106`).

## Related

- [[pages/invariants/no-executive-writes]] and [[pages/invariants/secrets-never-in-source]] list the tests that are missing for each rule.
