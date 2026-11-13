---
paths:
  - "src/Scout.Sandbox/**"
  - "tests/Scout.Sandbox.Tests/**"
---

# Sandbox

Owns one operation, `DockerSandboxRunner.RunTestsAsync(SandboxRequest, CancellationToken)` (`src/Scout.Sandbox/DockerSandboxRunner.cs:74`): pick the stack from the snapshot's manifests, copy the snapshot into a fresh work folder, restore, test, record the run beside its log, and answer what happened; and the same detection on its own, `DockerSandboxRunner.Detect(string)` (`src/Scout.Sandbox/DockerSandboxRunner.cs:168`), which runs nothing and writes nothing. Does not own when a run happens, the `Local container` grant, the test command from the conventions profile or the CI workflow, the snapshot itself, or what the card shows ([[pages/services/plugin-github]], [[pages/services/desktop-quick-ask]]). It references no other Scout project and is not referenced by the GitHub plugin: [[pages/services/host-console]] adapts it to the plugin's `ISandboxRunner` seam.

## Rules this project must keep

- [[pages/invariants/no-executive-writes]]: the only writes are the work folder, the log and the run's record under `WorkDirectory`, Scout's data folder (the record is descriptive: the repository, the pull request, the stack, the image and how the run ended, never the output or a credential); the container writes only the work folder, which is deleted. The library declares no function the model can call; the GitHub plugin's `run_pull_request_tests` declares the `Local container` row and runs it under the owner's grant.
- [[pages/invariants/secrets-never-in-source]]: no credential is read or passed; the owner's environment does not reach the container or the `docker` process beyond the client variables above.
- [[pages/invariants/pluggable-boundaries]]: no vendor SDK; the `docker` command line is the one dependency, which Podman's compatible command line can take later through `DockerPath`.

## Pages that govern this project

Read one when your change touches what its title names.

- [[pages/decisions/0043-the-verdict-on-a-pull-request|ADR 0043 - The verdict on a pull request, with the project's tests in an isolated container]] - the rules below are its decision; a change to a rule is a change to the ADR
- [[pages/decisions/0032-per-feature-write-permissions|ADR 0032 - Features declare what they write, and the owner grants each one]] - the run is a write under Scout's data folder, behind the `Local container` row the GitHub plugin declares
- [[pages/decisions/0039-repository-snapshot-under-the-data-folder|ADR 0039 - A repository snapshot under Scout's data folder, read locally and searched by the Knowledge index]] - the snapshot the run copies
- [[pages/invariants/no-executive-writes|Scout performs descriptive writes only]]
- [[pages/invariants/secrets-never-in-source|Secrets never live in source]]
- [[pages/invariants/pluggable-boundaries|Adapters, LLM providers and plugins are swapped by configuration]]
- [[pages/conventions/testing|Testing]]

## What to read for a change

Find the row your change matches and read that page. Read one or two rows, not the table.

| If the change touches | Read |
|---|---|
| Public contract | `context/pages/services/sandbox/public-contract.md` |
| mermaid; Stacks; Tests that need Docker; Scout's own repository in its sandbox; Java versions | `context/pages/services/sandbox/how-it-works-mermaid.md` |
| .NET, Node, Bun, Python and Go versions; Images; Layout | `context/pages/services/sandbox/how-it-works-net-node-bun-python-and-go-versions.md` |
| The rules | `context/pages/services/sandbox/the-rules.md` |
| Failures (Rule I) | `context/pages/services/sandbox/failures.md` |
| Hot spots | `context/pages/services/sandbox/hot-spots.md` |
| Cache footprint | `context/pages/services/sandbox/cache-footprint.md` |
| Cross-service dependencies | `context/pages/services/sandbox/cross-service-dependencies.md` |
| Testing footprint | `context/pages/services/sandbox/testing-footprint.md` |
| Known violations / P1 debt | `context/pages/services/sandbox/known-violations-p1-debt.md` |

For where a type lives or who calls it, ask the code graph (`graphify explain "<Type>"`, `graphify affected "<Type>"`).
