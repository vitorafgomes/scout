---
title: "Versions conventions"
version: "1.0"
date: 2026-11-13
changes: "Version notes moved out of page headers."
page_type: knowledge
status: active
description: "Version notes that were kept in page headers."
source: ["context/pages/"]
---

## Version notes: `pages/conventions/decisions-that-look-like-defects.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- v1.7 (2026-11-12): a record that fails while the worker runs leaves the function's result, one Warning and the started entry, which lists as not confirmed (issue #323); and a started write with no record is listed as may have run, never sent again. Earlier: v1.6 (2026-11-12): the shared control-plane test project. Earlier: v1.5 (2026-11-12): eighteen entries for findings answered as not a defect: the Slack stop, the Teams failed turn, the turn attachments, the empty comment list and read mark, the scheduler slot, the sync error, the projects validator, the readiness gate, the chat hand-over, the Node range, the control plane launcher environment, the collapsible list, the panel focus, the recording release, the review gate hook, the publish script and the Kubernetes skill. Earlier: v1.4 (2026-11-11): two presenter versions equal in every field share one id. Earlier: v1.3 (2026-11-11): the in-memory credential store leaves the open tensions for Control plane and hosts, ruled by its contract. Earlier: v1.2 (2026-11-11): the control plane's local-model writes leave the open tensions, ruled by the owner and recorded in ADR 0032.
- v1.1 (2026-11-11): the Desktop section, with the Models rows whose arrow keys move the focus without checking.
- v1.0: Initial established-patterns page, seeded from the audit findings of 2026-11-09 and 2026-11-10 that were answered as not a defect

## Version notes: `pages/conventions/desktop-prototype.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- v1.15 (2026-11-12): the Main artboard draws a review that found nothing.
- v1.14 (2026-11-12): the owner approved Main's job controls drawing with five answers; every artboard draws Main's bell panel; Main's Bell group draws the app-only states.
- v1.13 (2026-11-12): the Features artboard's row sentences cover every action of their row, GitHub draws Notifications, and the Kubernetes path pill is the detected path.
- v1.12 (2026-11-12): the Features artboard draws the GitHub panel's app-only states, the Remove the bot confirmation and the line after a removal as Preview states.
- v1.11 (2026-11-12): the Features artboard draws the Docker panel's app-only states and the app's repository detail line as Preview states.
- v1.10 (2026-11-12): the Features artboard draws the YouTube panel's app-only states and the Connect YouTube dialog's states as Preview states.
- v1.9 (2026-11-12): the Sources and Models artboards draw a model that cannot call tools, with the Preview states No tools and In use without tools.
- v1.8 (2026-11-12): the Sources artboard's Ollama dialog draws Pull by name and Browse the Ollama library, with six Preview states.
- v1.7 (2026-11-12): the Sources artboard's Ollama dialog lists every installed model, with the Preview state Installed outside Scout.
- v1.6 (2026-11-12): the Sources artboard draws the Ollama card's line after the provider in use moves away from Ollama, freed or could not ask, with two Preview states.
- v1.5 (2026-11-11): the Sources artboard's Ollama dialog draws the Skill matching row and the second download.
- v1.4 (2026-11-11): the AI Sources artboard loses the Local Models card, which the owner declined.
- v1.3 (2026-11-11): the Models artboard's Analysis rows and Translation modes take one row in the tab order and move the focus with the arrow keys without choosing.
- v1.2 (2026-11-11): the Meetings notice is built, its design mark removed.
- v1.1 (2026-11-08): link to the prototype migration audit and plan.
- v1.0: Initial page: the clickable prototype of the desktop lives in the context folder, and a change to a screen updates its artboard in the same branch

## Version notes: `pages/conventions/testing.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- A test that asserts a relative time pins the clock, so the minutes between module load and assertion cannot change the text; notification-bell.component.spec.ts pins Date for the whole file (2026-11-12). Earlier: A test that depends on a machine capability asserts an exact outcome per capability, chosen by an oracle independent of the code under test, never a skip; Scout.Sandbox.Tests recounted, 10 tests need Docker (2026-11-12, issue 372). Earlier: `Microsoft.NET.Test.Sdk` 18.12.0 (2026-11-12). Earlier: Tests that assert on a process-wide store run in a collection with parallelisation disabled (2026-11-11). Earlier: The fake worker answers a delete, and meetings-notices.spec.ts (2026-11-11). Earlier: The end-to-end flows gain the operator window's Back to Scout (2026-11-11). Earlier: Teardown runs in an afterEach hook, never as a test's last line; the quick-ask tests split into one file per module (2026-11-10). Earlier: The seeds of the loaded states are tracked files: the write log seed `writes.log` is excepted from the `*.log` ignore rule, and an ignored seed is checked for before a push (2026-11-09). Earlier: The end-to-end harness runs the real control plane host on a seeded data folder, with five specs for the loaded states and a host that dies; every test has its own runtime and temporary folder, runs at UTC and checks its isolation inside main at launch; the e2e job builds the host, pinned by the release-workflow self-test (2026-11-09). Earlier: CI's first step also runs the scout-review workflow self-test; the site tests cover the reference page's navigation, and doc/** triggers UI CI (2026-11-08). Earlier: CI's first step runs the next-version, with-test-version and release-workflow self-tests (2026-11-08). The packaged smoke test reads the fuses of the shipped binary and drives a copy with only the inspector fuse turned back on (ADR 0053, 2026-11-08). The persistence rules of tests/Scout.ArchTests/PersistenceTests.cs; Directory.Packages.props citations re-pointed (2026-11-08). The packaged smoke test: bun run test:smoke opens the extracted AppImage in network and PID namespaces and checks the window, the shipped worker and control plane, a clean startup and a missing worker (ADR 0051 amendment, 2026-11-08). Earlier: CI caches Electron and electron-builder downloads per OS, so a GitHub 5xx no longer fails the package step once warm (2026-11-08). Desktop end-to-end tests: Playwright drives the built app on an isolated profile with a fake worker and no network, six Chat flows, an e2e job on Linux under xvfb (ADR 0051, 2026-11-08). Earlier: the Whisper model switch integration test named as the guarantee that the worker running after a model change loads the model In use (2026-11-07). Earlier: Scout.Sandbox.Tests and its Docker end-to-end tests: filtered out by test-per-project.sh when no daemon answers, run by a CI step of their own; ArchitectureTests citations re-pointed. Earlier: line endings: LF on every checkout for the source-text ui tests, and a relative fixture that stays relative on Windows

## Version notes: `pages/conventions/tooling.md`

The notes kept in the `changes` field of the page header until 2026-11-13, newest first.

- v1.10 (2026-11-13): scripts/check-hidden-text.sh fails CI on text a person cannot see but a model reads. scripts/token-usage.sh and the scout-meter agent measure the tokens a session used, per agent, and the size of every Markdown document a model reads.
- v1.9 (2026-11-13): origin statements removed.
- v1.8 (2026-11-12): the gate for the pipeline skill is the grader table in tests/Scout.ArchTests and the live eval is advisory, with a test that fails on a grader with no accepted and rejected row; a test that every hook the settings run has a self-test CI runs; the Claude Code compatibility section; the outbound-call and Contracts eval graders reject an answer that denies what they look for; Stryker is a local tool pinned in the manifest; the mechanic proves red on the merge base; the refuter reads the reviewed tree and a finding it cannot locate stays unresolved.
- v1.7 (2026-11-11): every external action in the workflows is pinned to a commit with its release tag in a trailing comment, scripts/release-workflow.test.sh fails on one that is not, and Dependabot raises the pins weekly.
- v1.6 (2026-11-11): check-diagnostics counts a diagnostic without file coordinates (a project-level error, warning, message or info); install-git-hooks never backs up a hook it installed itself and never overwrites a backup; both scripts have a self-test, which CI runs in its first step.
- v1.5 (2026-11-11): the reviewer runs on opus only for the read-only and secrets lenses and on sonnet for invariants and boundaries; it reports at most five findings, checks the page of decisions that look like defects first, and names a guard that cannot fire.
- v1.4 (2026-11-08): the Release workflow runs CI and UI CI before it packages; the git guard asks on a stash that moves work; scout-review returns an unresolved list, pinned by .claude/workflows/scout-review.test.sh in CI; check-context-refs and test-per-project fail when they find nothing to check.
- v1.3 (2026-11-08): scripts/next-version.sh computes the next version from the commits since the last tag, scripts/with-test-version.sh gives every local test build its own version.
- v1.2 (2026-11-08): Playwright for the desktop end-to-end tests, with its private keyring and virtual display (ADR 0051).
- v1.1 (2026-11-06): the quality gates ported from acme: the gated analyzer rules in .editorconfig, AnalysisMode=Recommended, tests/Scout.ArchTests, self-tests for every hook, test-per-project.sh, the diagnostics gate and per-project tests in CI, gate 0 in the pipeline skill.
- v1.0 founded with the harness.
