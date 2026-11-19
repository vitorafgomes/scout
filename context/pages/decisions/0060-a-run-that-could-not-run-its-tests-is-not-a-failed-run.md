---
title: "ADR 0060 - A run that could not run its tests is not a failed run"
version: "1.1"
date: 2026-11-15
updated: 2026-11-19
changes: "v1.1 (2026-11-19): amendment, the desktop reads unrunnable. v1.0: the decision as first written"
page_type: adr
status: accepted
description: "A container run whose failed tests all needed the Docker daemon the container does not have is reported as unrunnable, with how many, and never as failed; nothing is filtered out by a name the snapshot gives."
supersedes: ""
superseded_by: ""
source:
  - chat
  - src/Scout.Sandbox/DockerSandboxRunner.cs
  - src/Scout.Plugin.GitHub/GitHubActionsPlugin.Tests.cs
  - tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs
  - tests/Scout.Plugin.GitHub.Tests/GitHubActionsPluginRunTestsTests.cs
reliability: high
---

# ADR 0060 - A run that could not run its tests is not a failed run

> A container run whose failed tests all needed the Docker daemon the container does not have is reported as unrunnable, with how many, and never as failed; nothing is filtered out by a name the snapshot gives.

## Status

Accepted on 2026-11-15 by the owner. It extends [[pages/decisions/0043-the-verdict-on-a-pull-request]], whose v1.3 amendment leaves out the tests a repository marks `Category=Docker`; that filter stays as it is.

## The problem

A review of a repository whose integration tests need a Docker daemon ran `dotnet test` over the whole solution in Scout's container, which has no daemon and no network. Hundreds of tests passed and every integration test failed on the ping of the daemon. The card said the tests failed and still offered an approval: the tests had not failed, Scout could not run them.

## The decision

- **The third outcome.** `SandboxResult.Unrunnable` (`src/Scout.Sandbox/SandboxRun.cs:27`) is null, or the sentence that says what could not run. `ExitCode` keeps the real exit code; the run's record still says `failed`.
- **When it is set.** Only on a test command that exited non-zero, other than 126 or 127 (`src/Scout.Sandbox/DockerSandboxRunner.cs:143`), when all of these hold over the kept output, its last 200,000 characters (`:326`):
  - it holds at least one failed test's block, from a `Failed <test>` line to the next one or to a `Passed!`/`Failed!` summary line (`:314`, `:317`);
  - every block carries one of `DotNet.Testcontainers`, `Docker.DotNet`, "Cannot connect to the Docker daemon", "Docker is either not running or misconfigured" or `docker.sock`, as the tools print them (`:305`, `:347`);
  - at least one assembly's summary line says `Failed!`, and every assembly that failed passed no test (`:352`, `:356`).
- **The sentence** (`:362`), N the sum of the failed assemblies' `Failed:` counts and M the blocks the kept output holds: "N test(s) could not run: they need a Docker daemon, which the container does not have." when M is N or more; "N test(s) did not pass; the M whose failure is in the kept output could not run: they need a Docker daemon, which the container does not have." when fewer.
- **Where it is carried.** The host adapter copies it (`src/Scout.Host.Console/SandboxRunnerAdapter.cs:32`) into the plugin's `SandboxRunResult` (`src/Scout.Plugin.GitHub/Abstractions/ISandboxRunner.cs:43`); `run_pull_request_tests` answers it as `unrunnable`, and its description tells the model that such a run did not fail, to say which tests could not run and whether the commit's checks cover them (`src/Scout.Plugin.GitHub/GitHubActionsPlugin.Tests.cs:59`); the run kept for the head commit carries it (`:249`), so the scout-findings block's `tests` object writes `unrunnable` when set (`src/Scout.Plugin.GitHub/ReviewFindings.cs:274`).

## Calls that leave the process

None new. The rule reads the output the run already captured.

## What follows

- **Gained:** a run that could not run its tests is no longer a failure in the model's answer or in the review's data.
- **Paid:** the desktop's card and the verdict's tests line still say "Tests failed (exit 1)" until the desktop reads `unrunnable` ([[pages/roadmap/followups]]).
- **To watch:** a test project that mixes passing tests with tests that need Docker stays failed; the rule reads VSTest's output of `dotnet test` only, so the other stacks and the Microsoft Testing Platform stay failed; a test host that crashes before its summary lines stays failed. Each errs on the side of a failure, never of a hidden one.

## Rejected options

- **Leaving the Testcontainers projects out with `--filter 'FullyQualifiedName!~<assembly name>'`.** Built and taken out before it shipped: VSTest's `~` is a substring match with no anchored form, and the pull request under review controls the project file, so an assembly name that is a substring of other tests' names (a root namespace) could leave out every test and make the run pass. No check on the name closes that.
- **Requiring every failure's block in the kept output.** The output is a tail; the common case has more failures than the tail holds, so the run would almost never qualify. The sentence says how many failures were read instead.

## How it is held

- `tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:369`: the bug's output gives the count of the kept blocks and keeps exit 1; `:383`, the sentence when every failure is in the kept output; `:396`, exit 0, 126 and 127 give null; `:405`, an assembly that failed and also passed tests; `:423`, a real failure beside the Docker assembly; `:432`, the signature only outside the blocks; `:443`, an output cut before any block; `:454`, a signature in a log line after the summary; `:465`, blocks without a summary line; `:477`, no signature, a pass and a signature outside any failed test.
- `tests/Scout.Plugin.GitHub.Tests/GitHubActionsPluginRunTestsTests.cs:140`: the answer, the kept run and the block carry `unrunnable`; `:160`, absent on a passing and on a failed run.
- `tests/Scout.Host.Console.Tests/HostSandboxCompositionTests.cs:90`: the adapter carries it to the plugin.

## Links

- Projects it shapes: [[pages/services/sandbox/public-contract]], [[pages/services/plugin-github/the-pull-request-s-checks-and-its-tests-in-a-con]]
- Decisions it extends: [[pages/decisions/0043-the-verdict-on-a-pull-request]]; beside [[pages/decisions/0058-an-approval-needs-every-check-passed]], which refuses an approval over a failed check or a conflict

## Amendments

### 2026-11-19 - The desktop reads `unrunnable`

- **Replaces, under "What follows",** the **Paid** sentence "the desktop's card and the verdict's tests line still say "Tests failed (exit 1)" until the desktop reads `unrunnable` ([[pages/roadmap/followups]])." It now reads: "**Paid:** nothing on the desktop: the review card says "Tests could not run" on a neutral chip with the worker's sentence under the chips, and the tests line a verdict posts reads "Tests could not run: <sentence>", never "Tests failed"; the run's last output is still shown."
- **Where it holds:** `ReviewTests.unrunnable`, `ui/packages/shared/src/ipc-contract.ts:1704`; the parser, which keeps it only as text and only on a run that exited non-zero, `ui/packages/renderer/src/app/core/review-findings.ts:279`; the tests line, `:109`; the chip, its tone and the sentence, `ui/packages/renderer/src/app/core/review-findings.component.ts:116`, `:1377`, `:1382`; the Main artboard's state `awTestsUnrunnable`. The verdict buttons are unchanged: the commit's checks alone decide whether Approve is offered. It is described on [[pages/services/desktop-quick-ask/overview-replies]].
- **How it is held:** `ui/packages/renderer/src/app/core/review-findings.spec.ts:302`, kept on a failed run, dropped on a passing run and when it is not text; `:438`, the tests line alone and after the checks; `ui/packages/renderer/src/app/core/review-findings.component.spec.ts:1307`, the chip, its tone, the sentence, Last output, Approve offered and the line the approval posts; `ui/e2e/tests/unrunnable-tests-card.spec.ts`, the same card over the built app.
