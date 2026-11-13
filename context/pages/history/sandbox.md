---
title: "Sandbox - history"
version: "1.0"
date: 2026-11-10
changes: "Initial history note: a test command that could not be started says so"
page_type: reference
status: artefact
description: "What changed in Scout.Sandbox and when."
source:
  - chat
  - src/Scout.Sandbox/DockerSandboxRunner.cs
  - tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs
reliability: high
---

# Sandbox - history

> Dated changes to [[pages/services/sandbox]]; the service page states how it works now.

## 2026-11-10 - A run the owner stops leaves no record

- **Asked for:** the owner's Stop on a running test run removes only Scout's own container and records no result.
- **What changed:** nothing in the runner: a cancelled token already killed the container by name and deleted the folder. A test now locks it for the test phase: the kill runs on a token of its own, the container was started with `--rm`, and no `.run.json` is written.
- **Tests:** `A_run_the_owner_stops_during_the_tests_kills_its_self_removing_container_with_a_token_of_its_own_and_keeps_no_run_record` in `DockerSandboxRunnerTests.cs`.

## 2026-11-10 - A test command the shell could not start says so

- **Asked for:** the owner could not tell from a review whether the tests failed or never started: every non-zero exit read "Tests failed (exit N)".
- **What changed:** a test command that exits 126 or 127, the shell's codes for a command not executable or not found, answers `Reason` "The test command could not be started (exit N: a command it names was not found or is not executable).", with `Ran` true and the exit code, so the result, the model's answer and the run's record carry it; the record's `result` stays `failed`.
- **Not changed:** exit 2, which `sh` also gives for a line it cannot parse, is not told apart, because test runners such as pytest use it for their own errors. A failed restore already carried "The restore exited with code N; the tests did not run.". The desktop's review line still reads only the exit code when `ran` is true, a follow-up in [[pages/roadmap/followups]].
- **Tests:** `A_test_command_the_shell_could_not_start_says_so_apart_from_tests_that_failed` in `DockerSandboxRunnerTests.cs`: exits 127 and 126 carry the sentence, exits 2 and 1 carry none.

## Version notes: `pages/services/sandbox.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- v1.12 (2026-11-13) the Docker runs switch the host reads once also reaches the audit's review environment, for the check before a pull request review (ADR 0055). Earlier: A failed run whose output is longer than the 4,000 characters the result carries (a restore or a test command that exits non-zero, or a run past the time limit) leads the result with the lines before them that name the failure, at most 2,000 characters with a marker for the rest, then the 4,000; a passing run is unchanged (2026-11-12). Earlier: The .NET default test command leaves out the tests a repository marks `Trait(\"Category\", \"Docker\")`, found by reading its `*.cs` files as text, and the run's note says so with the count of files; a caller's command is never altered and its note says nothing was left out; Scout's own repository ran green in Scout's sandbox on 2026-11-12 (issue 372). Earlier: A run the owner stops from Scout Desktop, through the caller's token, kills its self-removing container and leaves no run record, locked by a test (2026-11-10). Earlier: A test command that exits 126 or 127, the shell's codes for a command not executable or not found, answers a Reason saying it could not be started, Ran still true; the GitHub plugin no longer passes a CI line that cannot run over a fresh snapshot (2026-11-10). Earlier: the detection is public for the host (`DockerSandboxRunner.Detect`: the stack, the image, the version in it and each declared version with its file, or the refusal and the stack that refused), which the GitHub plugin records per repository on every snapshot; every run that reached its work folder writes `<id>.run.json` beside its log (repository, pull request, stack, image, passed, failed or notRun with the reason, exit code, duration, time), and the request carries the repository and the pull request (ADR 0047 v1.4). Earlier: .NET, Node, Bun, Python and Go run on the version the project declares, from tables of pinned official images (SDK 8, 9, 10; Node 18 to 26; Bun 1; Python 3.10 to 3.14; Go 1.23 to 1.26), or a refusal naming both versions; nothing declared keeps the old default and says so; every result whose container ran carries a Note naming the version and where it came from; a stack that refuses ends the detection; a project without a framework takes the nearest `Directory.Build.props` that declares one; the SDK 8 restore marks NuGet's migration done because the SDK 8 first run fails for a uid outside the image's passwd (SDK 9 and 10 need nothing); the GitHub plugin runs a `./gradlew` in command position as the image's offline gradle. Earlier: Scout Desktop owns the work folder and passes it as WorkDirectory, and the host adapter runs nothing while Scout:Sandbox:Enabled is not true (ADR 0047 v1.2). Java: Maven and Gradle on the JDK and the Gradle major the project declares, from a table of pinned official images, or a refusal naming both versions; a work folder Docker cannot see (Docker Desktop file sharing) says where to share it. Earlier: the host composes it when GitHub is configured and adapts it to the GitHub plugin's ISandboxRunner. Earlier: Image and Command are null when nothing ran. Earlier: Bun repositories run in a pinned oven/bun image; the result carries a Note (the .NET runs say they used the .NET 10 SDK with roll-forward); test-per-project.sh and CI for the Docker tests. Earlier: initial service page: the Docker test runner of ADR 0043, its stacks, its pinned images and its isolation rules
