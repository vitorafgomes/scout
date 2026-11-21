---
title: "Sandbox: the test summary reader"
version: "1.0"
date: 2026-11-21
updated: 2026-11-21
changes: "v1.0: the page as first written"
page_type: service
status: active
description: "Read when a change touches how a test run's output is read: each test project's counts read as the output flows, its bounds, and when a run could not run its tests."
source: ["src/Scout.Sandbox/DockerSandboxRunner.cs", "tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs"]
reliability: high
---

# Sandbox: the test summary reader

> A run's output is read piece by piece as it arrives, so each test project's summary is counted however long the output, and a run whose failures all needed the Docker daemon is said to have could not run, never to have failed ([[pages/decisions/0060-a-run-that-could-not-run-its-tests-is-not-a-failed-run]]).

## The reader

- `DockerSandboxRunner.TestSummaryReader` (`src/Scout.Sandbox/DockerSandboxRunner.cs:338`) reads a `dotnet test` output as it flows: each test project from its summary line, with its passed, failed and skipped counts, and the failed tests' blocks printed before it, each block deciding as it ends whether that failure needed the missing daemon. It keeps counts and flags, never the text.
- **Bounds:** one line of at most 4,096 characters (`:328`), the rest of a longer line not read; at most 200 projects (`:325`), and when more printed a summary the run's sentence says only the first 200 are counted (`:322`). A line is put together across pieces, a character split between two pieces included.
- **Could not run** (`:423`): a run is unrunnable when at least one project could not run and none failed for real; such a run is never reported as failed. The run's sentence says how many passed, how many failed and where, and how many could not run.

## For the review

A review job runs the pull request's tests itself, through `run_pull_request_tests` under the GitHub feature's "Local container" grant; the counts reach the review as `TestRunFacts`, and tests that could not run here are covered for the offer by the pull request's own checks when every check passed ([[pages/decisions/0070-one-verdict-decision-and-no-approval-on-its-own]]).

## Tests

`tests/Scout.Sandbox.Tests/DockerSandboxRunnerTests.cs:466` (a summary line split across two pieces counted once), `:480` (a failed block split mid-line still names the missing daemon), `:495` (a line longer than the bound), `:507` (a last line without a break, and more summaries than counted).
