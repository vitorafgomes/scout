---
title: "Sandbox"
version: "1.12"
date: 2026-11-07
updated: 2026-11-12
changes: "v1.12 (2026-11-13) the Docker runs switch the host reads once also reaches the audit's review environment, for the check before a pull request review (ADR 0055).."
page_type: service
status: active
description: "Runs a repository snapshot's tests in a disposable Docker container on the owner's machine, through the docker command line, under fixed isolation rules: the work folder under Scout's data folder is the only mount, the image is pinned by digest, the tests run without the network, as an unprivileged user on a read-only filesystem with every capability dropped and capped pids, memory, CPU and time."
source:
  - src/Scout.Sandbox/
  - tests/Scout.Sandbox.Tests/
  - context/pages/decisions/0043-the-verdict-on-a-pull-request.md
  - chat
reliability: high
---

# Sandbox

> Runs a repository's own tests in a disposable container that never sees the host, a secret of the owner's or, during the tests, the network.

The owner on 2026-11-07: "the user having a Docker where Scout runs everything isolated". The container is the security boundary of [[pages/decisions/0043-the-verdict-on-a-pull-request]]: a pull request's code is untrusted, and its tests are ordinary files in the diff.

## Responsibility

Owns one operation, `DockerSandboxRunner.RunTestsAsync(SandboxRequest, CancellationToken)` (`src/Scout.Sandbox/DockerSandboxRunner.cs:75`): pick the stack from the snapshot's manifests, copy the snapshot into a fresh work folder, restore, test, record the run beside its log, and answer what happened; and the same detection on its own, `DockerSandboxRunner.Detect(string)` (`src/Scout.Sandbox/DockerSandboxRunner.cs:181`), which runs nothing and writes nothing. Does not own when a run happens, the `Local container` grant, the test command from the conventions profile or the CI workflow, the snapshot itself, or what the card shows ([[pages/services/plugin-github]], [[pages/services/desktop-quick-ask]]). It references no other Scout project and is not referenced by the GitHub plugin: [[pages/services/host-console]] adapts it to the plugin's `ISandboxRunner` seam.

## Rules

The rules this project must keep, and what to read before changing it, are in `.claude/rules/sandbox.md`. Claude Code loads that file when a task touches the project.

## Topics

Read the page your change touches, not all of them.

| Page | Read when a change touches | Characters |
|---|---|---|
| [[pages/services/sandbox/public-contract\|Public contract]] | Public contract | 7890 |
| [[pages/services/sandbox/how-it-works-mermaid\|How it works: mermaid]] | mermaid; Stacks; Tests that need Docker; Scout's own repository in its sandbox; Java versions | 10901 |
| [[pages/services/sandbox/how-it-works-net-node-bun-python-and-go-versions\|How it works: .NET, Node, Bun, Python and Go versions]] | .NET, Node, Bun, Python and Go versions; Images; Layout | 9034 |
| [[pages/services/sandbox/the-rules\|The rules]] | The rules | 5584 |
| [[pages/services/sandbox/failures\|Failures (Rule I)]] | Failures (Rule I) | 3400 |
| [[pages/services/sandbox/hot-spots\|Hot spots]] | Hot spots | 693 |
| [[pages/services/sandbox/cache-footprint\|Cache footprint]] | Cache footprint | 450 |
| [[pages/services/sandbox/cross-service-dependencies\|Cross-service dependencies]] | Cross-service dependencies | 811 |
| [[pages/services/sandbox/testing-footprint\|Testing footprint]] | Testing footprint | 9056 |
| [[pages/services/sandbox/known-violations-p1-debt\|Known violations / P1 debt]] | Known violations / P1 debt | 2203 |

## Related

Map: [[pages/architecture/project-map]]. Decision: [[pages/decisions/0043-the-verdict-on-a-pull-request]].
