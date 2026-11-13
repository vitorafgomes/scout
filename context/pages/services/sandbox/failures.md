---
title: "Sandbox: Failures (Rule I)"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Sandbox service page."
page_type: service
status: active
description: "Read when a change touches: Failures (Rule I)."
source: - src/Scout.Sandbox/ - tests/Scout.Sandbox.Tests/ - context/pages/decisions/0043-the-verdict-on-a-pull-request.md - chat
reliability: high
---

## Failures (Rule I)

| Failure | Answer | Test |
|---|---|---|
| `docker` missing from the PATH (the process cannot start) | `Ran=false`, "Docker is not installed or not running.", no folder created | `tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:391`; with the real `Process`, `tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:592` |
| The daemon does not answer (`docker version` exits non-zero) | the same sentence | `tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:405` |
| The restore exits non-zero | `Ran=true`, its exit code, the restore command and its output, "The restore exited with code N; the tests did not run." | `tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:417`; a build's own output that mentions a bind source but exits 1 stays this answer, `tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:469` |
| Docker cannot mount the work folder (exit 125, "bind source path does not exist" or "mounts denied": Docker Desktop on Linux runs its engine in a VM that sees only the folders under Settings, Resources, File sharing, by default the home folder) | `Ran=false`, "Docker cannot see Scout's work folder <WorkDirectory>; with Docker Desktop, add it under Settings, Resources, File sharing.", never the daemon's text as the reason | `tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:444` |
| The test command exits 126 or 127 (not executable, not found: a runner the image lacks, a script that is not there) | `Ran=true`, the exit code, "The test command could not be started (exit N: a command it names was not found or is not executable)."; the record's `result` stays `failed` | `tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:644` |
| Past 20 minutes | `docker kill`, `Ran=false`, "The tests ran past the 20-minute limit." | `tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:489` |
| The run's record cannot be written | a `Warning` log line; the result, the log and the deleted work folder as without it | `tests/Scout.Sandbox.Tests/SandboxRunRecordTests.cs:129` |
| The caller cancels | `docker kill`, the folder deleted, `OperationCanceledException` | `tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:521` |
| The owner stops the run from Scout Desktop, which cancels the caller's token during the tests (2026-11-10) | `docker kill` of the test container on a token of its own, the container started with `--rm` so Docker removes it, the folder deleted, no `.run.json` written because a stopped run is no result, `OperationCanceledException` | `tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:542` |
| `WorkDirectory` inside the snapshot (the copy would copy itself until the path is too long) | `Ran=false`, "The sandbox folder is inside the repository snapshot, so the copy would never end.", before any process or copy (`src/Scout.Sandbox/DockerSandboxRunner.cs:93`) | `tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:609`; a sibling folder with the same prefix still runs, `tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:625` |
