---
title: "ADR 0043 - The verdict on a pull request, with the project's tests in an isolated container"
version: "1.0"
date: 2026-11-07
changes: "Initial ADR draft"
page_type: adr
status: proposed
description: "Scout approves a pull request or requests changes as a declared write the owner approves per action, and the verdict carries the project's tests: the pull request's check runs first, then the project's own test command run in a disposable container on the owner's machine under fixed isolation rules."
supersedes: ""
superseded_by: ""
source:
  - chat
  - src/Scout.Plugin.GitHub/GitHubWriteClient.cs
  - src/Scout.Plugin.GitHub/GitHubServiceCollectionExtensions.cs
  - src/Scout.ControlPlane/WriteGate/WriteGateService.cs
  - src/Scout.Skills/skills/code-review.md
reliability: high
---

# ADR 0043 - The verdict on a pull request, with the project's tests in an isolated container

> Scout says whether a pull request is good to merge: approve or request changes, a declared write the owner approves on the card, backed by the pull request's check runs and, when those do not run the tests, by the project's own test command run in a disposable container that never sees the host, a secret or the network during the tests.

## Status

- [x] Proposed
- [ ] Accepted
- [ ] Deprecated / superseded by

Updated: 2026-11-07. Gap plan G8, the next deliverable after G3 ([[pages/roadmap/gap-plan]]). Waits for the owner's approval before any code; two choices are his to confirm: Docker as the one supported runtime at first, and no network inside the container once the dependencies are restored.

## Context

The owner on 2026-11-07: "o Scout tem que poder aprovar um PR ou rejeitar, esse é o objetivo, validar tudo"; "tem que executar os testes necessários, com isso às vezes ele tem que ter o mínimo na máquina para fazer isso"; "o usuário ter um docker onde o Scout execute tudo isolado"; and "nosso próximo entregável tem que fazer tudo isso". Today the review is a commenting review only: `submit_review` posts `pulls/{n}/reviews` with the event `COMMENT` (`src/Scout.Plugin.GitHub/GitHubWriteClient.cs:10`, `:29`, the event at `:67`), declared as a write on the `github` feature (`src/Scout.Plugin.GitHub/GitHubServiceCollectionExtensions.cs:92`), and the skill says "You never edit files, commit, merge or approve anything yourself" (`src/Scout.Skills/skills/code-review.md:78`). Scout reads nothing about the pull request's checks, and runs nothing: the vision ([[pages/overview/vision]] v1.2) now says a project's tests run only in isolation, never on the host.

Three facts shape the decision:

- A pull request's code is untrusted. A test that exfiltrates a token, mines, or deletes is an ordinary file in the diff. The place the tests run is the security boundary, not a convenience.
- Most repositories already run their tests on the pull request. The check runs are a read, cost nothing to install, and are the first signal.
- The write contract ([[pages/decisions/0032-per-feature-write-permissions]]) already has the shape: a feature declares its calls, a write runs under the owner's grant, an irreversible action runs after the owner approves that action, and `WriteGateService.ClaimNextApprovedAsync` hands approved actions to their executor (`src/Scout.ControlPlane/WriteGate/WriteGateService.cs:71`). An approval or a request for changes on someone's pull request is such an action.

## Decision

- **The verdict is an event on the existing review.** `submit_review` gains a `verdict` argument: `comment` (today's behaviour), `approve` or `request_changes`, mapped to the GitHub review event. `comment` stays a `Write` under the feature's grant; `approve` and `request_changes` are `IrreversibleWrite` on the `Pull requests` row of the `github` feature and run only after the owner approves that action, which is his click on the card's Approve or Request changes button in the desktop, recorded in the write log like the operator's approvals ([[pages/decisions/0033-channel-operator-architecture]]). The model never chooses the verdict that goes out: it proposes one in the card's summary, the owner's click decides. GitHub refuses an approval by the pull request's own author, so on the owner's own pull requests the card offers Request changes and Comment only.
- **The tests line comes first from the pull request's checks.** A new read function `get_pull_request_checks(owner, repo, number)` reads the head commit's check runs and statuses (`GET repos/{o}/{r}/commits/{sha}/check-runs` and `/status`, both GETs on the Pull requests scope, read access) and answers each check's name, conclusion and link. `present_review_findings` gains `checks_json`, so the card shows passed, failed with its link, or pending, per check. A pull request whose checks run the tests needs nothing on the owner's machine.
- **Then, when asked, the project's own tests in a container.** A new library `src/Scout.Sandbox` (with `tests/Scout.Sandbox.Tests`) runs one operation: the project's test command inside a disposable container on the owner's machine, driven by the `docker` command line, the one requirement (Podman's compatible command line comes later, through the same seam). The GitHub plugin calls it for a pull request when the repository has no check that runs the tests, or when the review asks for one more check, and only when the owner granted the feature's new `Local container` row (`Write`: it writes under Scout's data folder and nowhere else; reversible, so no per-action approval; without the grant, nothing runs and the verdict says so).
- **The container's rules are the security boundary.** Fixed in code, not in configuration, and tested one by one:
  - the snapshot of the pull request's head ([[pages/decisions/0039-repository-snapshot-under-the-data-folder]]) is copied into a fresh work folder under Scout's data folder, which is the only thing mounted, at `/work`;
  - the image comes from a stack detected from the manifests and an allowlist Scout ships, pinned by digest, with defaults for .NET, Node, Python and Go; no image from the repository's own files;
  - two runs: restore with the network, then the tests with `--network none`; the owner may turn the restore's network off too, and then a repository without a restored cache does not run;
  - `--rm`, an unprivileged user, `--read-only` with the work folder and a `tmpfs` `/tmp` writable, `--cap-drop ALL`, `--security-opt no-new-privileges`, a pids limit, a memory cap (default 4 GB), a CPU cap (default 2), a wall-clock limit (default 20 minutes, then `docker kill`);
  - no environment variable, file or secret of the owner's enters; the command runs with an empty environment plus the stack's own variables;
  - the output is captured, cut at 200,000 characters, kept with the run under the data folder, and never fed back to the model as instructions: it is data in the card's tests line;
  - the work folder is deleted after the run.
- **The test command comes from the project, in this order:** the conventions profile's test line when the owner has one ([[pages/decisions/0040-review-findings-as-cards-in-chat]] v1.2 learns the profile); else the CI workflow's `run:` steps that invoke the stack's test runner (`dotnet test`, `npm test`, `pytest`, `go test`); else the stack's default. The command that ran is shown on the card and written to the write log.
- **The card.** The review card gets a tests line (checks and the container run, with "did not run" and why when either is missing) and, in the footer, Approve and Request changes beside Post; a click sends the verdict with the chosen findings, through the same ask path the posting uses today ([[pages/decisions/0040-review-findings-as-cards-in-chat]] v1.1).
- **The skill.** `code-review` keeps its prose and gains two things: it calls `get_pull_request_checks` before judging, and its verdict sentence names what the tests said. The sentence "never approve anything yourself" stays: the owner's click is the approval.

## Consequences

- **Better:** the owner's goal: a verdict, with evidence, on every pull request from chat; a repository with a working CI needs nothing installed; a repository without one gets its tests run without touching the host.
- **Better:** the isolation rules are in one library with one test per rule; a change to them is a change to this ADR.
- **Worse:** Docker becomes a requirement for the container run, and the owner's machine spends CPU and disk on other people's builds; the caps bound it and the grant is explicit.
- **Worse:** a project whose tests need a service (a database, a browser) does not run in the container; the verdict says so and rests on the checks and the reading. A compose file is a later ADR.
- **Neutral:** `comment` reviews, the audit and the issues change nothing.

## Alternatives considered

- **Rely on the pull request's CI only.** Rejected as the only path: many repositories have no CI, or a CI that builds but does not test; it stays the first signal.
- **Run the tests on the host under a restricted user.** Rejected: the code is untrusted and a user account is not a boundary the owner can reason about.
- **A Docker SDK or the Docker Engine API.** Rejected: the command line is enough, keeps the dependency list unchanged, and is what Podman emulates.
- **Scout decides the verdict itself.** Rejected: ADR 0032's rule that the model and the chat can only request; the owner's click is the decision.

## Enforcement

Tests to write with the change, in the words of the requirement:

- `tests/Scout.Sandbox.Tests`: one test per rule above, each asserting the `docker` arguments the run would issue (a fake runner records them): the work folder is the only mount; the image is from the allowlist by digest; the tests run with `--network none`; the user is unprivileged; the filesystem is read-only but the work folder and `/tmp`; every capability dropped; the pids, memory, CPU and time caps; an empty environment; the output cut at 200,000 characters; the work folder deleted. Plus the failing-call test (Rule I): `docker` missing, exiting non-zero, or hanging past the limit, and what the caller reports.
- `tests/Scout.Plugin.GitHub.Tests`: `approve` and `request_changes` are declared `IrreversibleWrite` and refused without an approved action; `comment` runs under the grant as before; the author's own pull request offers no approval; `get_pull_request_checks` is a GET and answers each check's conclusion; the container run never starts without the `Local container` grant; the command order (profile, CI workflow, default).
- `tests/Scout.Skills.Tests/BundledSkillsTests.cs`: the review skill calls the checks function and never approves by itself.
- `ui/`: the tests line and the two verdict buttons on the card, in the matrix spec; an approval click sends the verdict with the chosen findings.

## Related

- [[pages/roadmap/gap-plan]] G8; [[pages/roadmap/future-improvements]] F10 (the dependency graph)
- [[pages/decisions/0032-per-feature-write-permissions]], [[pages/decisions/0040-review-findings-as-cards-in-chat]], [[pages/decisions/0039-repository-snapshot-under-the-data-folder]], [[pages/decisions/0033-channel-operator-architecture]]
- [[pages/services/plugin-github]], [[pages/services/skills]], [[pages/services/desktop-ui]], [[pages/overview/vision]]
