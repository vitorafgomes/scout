---
title: "Sandbox: The rules"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Sandbox service page."
page_type: service
status: active
description: "Read when a change touches: The rules."
source: - src/Scout.Sandbox/ - tests/Scout.Sandbox.Tests/ - context/pages/decisions/0043-the-verdict-on-a-pull-request.md - chat
reliability: high
---

## The rules

Each rule is fixed in code and asserted on the `docker` arguments a fake `IProcessRunner` records.

| Rule (ADR 0043) | Where | Test |
|---|---|---|
| The work folder, under the configured root, is the only mount, at `/work`; no `-v`, `--volume` or `--volumes-from` | `src/Scout.Sandbox/DockerSandboxRunner.cs:188` | `tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:60` |
| The snapshot is copied without its symbolic links | `src/Scout.Sandbox/DockerSandboxRunner.cs:293` | `tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:109` |
| The image is the stack's allowlisted image, by digest | `src/Scout.Sandbox/DockerSandboxRunner.cs:202` | `tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:134`, `tests/Scout.Sandbox.Tests/SandboxStacksTests.cs:263` |
| Restore with the network, tests with `--network none`; `RestoreNetwork=false` takes the network from the restore too | `src/Scout.Sandbox/DockerSandboxRunner.cs:181` | `tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:148`, `tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:159` |
| `--rm`, and an unprivileged user: the owner's own uid and gid on Linux, 1000:1000 elsewhere, never root | `src/Scout.Sandbox/DockerSandboxRunner.cs:206` | `tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:168`, `tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:189` |
| `--read-only`, writable only `/work` and a `tmpfs` `/tmp` (`rw,exec,nosuid,nodev`) | `src/Scout.Sandbox/DockerSandboxRunner.cs:189` | `tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:195` |
| `--cap-drop ALL`, `--security-opt no-new-privileges`, no `--cap-add`, `--privileged` or `--device` | `src/Scout.Sandbox/DockerSandboxRunner.cs:191` | `tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:208` |
| `--pids-limit 512`, `--memory 4g`, `--cpus 2` | `src/Scout.Sandbox/DockerSandboxRunner.cs:32` | `tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:223` |
| 20 minutes for the whole run, then `docker kill` of the running container by its name | `src/Scout.Sandbox/DockerSandboxRunner.cs:35` | `tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:489` |
| No variable of the owner's enters: the container gets only the stack's `-e NAME=value` pairs, the `docker` process only `PATH`, `HOME`, the Windows profile folders, `TMP`, `XDG_RUNTIME_DIR` and the `DOCKER_*` client variables | `src/Scout.Sandbox/DockerSandboxRunner.cs:43`, `src/Scout.Sandbox/ProcessRunner.cs:43` | `tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:236`, `tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:259`, `tests/Scout.Sandbox.Tests/SystemProcessRunnerTests.cs:17` |
| The output is read in chunks and cut while reading to its last 200,000 characters, kept in the run's log; the result carries the last 4,000 | `src/Scout.Sandbox/DockerSandboxRunner.cs:29` | `tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:362` |
| A failed run (restore or test exit not 0, or past the time limit) whose output is longer than 4,000 characters leads its result with the lines before them that name a failure, one pattern set for every stack: a line holding `[FAIL]`, `Failed!`, `--- FAIL`, `FAILED` as a word, `<<< FAILURE!`, `panicked at` or an `Assertion...Error`; one starting (after spaces) with `Failed `, with `FAIL` as a word or with `● ` (Jest); `[ERROR] Tests run:` (Maven) and pytest's `E   ` line at the start; an `error` followed by a tool code (`error CS0246`, `error NU1101`); and an `Error Message:` line (VSTest) or a lowercase `failed <name>` line (Microsoft Testing Platform) with the two lines after it. Each is kept once, the first ones up to 2,000 characters, then "(N more failure lines left out)"; then "End of the output:" and the 4,000; at most 6,500 in all. No failure line found, or output of 4,000 or less, returns what a passing run returns, so no line appears twice; a passing run is unchanged whatever it printed. A solution-wide `dotnet test` prints a failing test mid-stream, and the last 4,000 characters of a large run hold only the later projects' summaries | `src/Scout.Sandbox/DockerSandboxRunner.cs:385`, `src/Scout.Sandbox/DockerSandboxRunner.cs:376` | `tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:684`, the bound `tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:717`, no failure line `tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:731`, a short output `tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:742`, the time limit `tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:753`, a passing run that printed a failure-like line `tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:780`, a failed restore `tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:792`, the line each runner prints `tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:827`; in Docker on VSTest `tests/Scout.Sandbox.Tests/DockerEndToEndTests.cs:438` and on the Microsoft Testing Platform `tests/Scout.Sandbox.Tests/DockerEndToEndTests.cs:483` (the .NET default command does not run that platform, [[pages/roadmap/followups]]) |
| The work folder is deleted after the run, whatever happened | `src/Scout.Sandbox/DockerSandboxRunner.cs:176` | `tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:98` and every failure test |
