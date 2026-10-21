---
name: scout-pipeline
description: Scout task orchestration. Always use this skill BEFORE writing or editing any code in this repo - for bug fixes ("fix", "broken", "error", "wrong value", "doesn't work"), features ("add", "implement", "create", "new adapter", "new plugin", "new provider"), refactors ("clean up", "restructure", "rename", "extract"), and diagnostics ("why", "investigate", "debug", "what causes"). Use even when the user gives a plain one-line request with no command. When in doubt whether a code task is being requested, use this skill.
---

# Scout task pipeline

This file is the single source of the pipeline. Root `CLAUDE.md` and the vault point here; a
disagreement between them and this file is fixed here, in the same change that found it. Repo facts
live in the vault (`vault/index.md`); the team rules A to J live in root `CLAUDE.md`; who runs on
which model is on `vault/pages/conventions/tooling.md`.

Scope: an open-source, read-only chatops assistant. The manager (the session thread) classifies,
reads the vault, states the plan, dispatches `scout-implementer` with it and checks the result
against the plan line by line, reporting any departure to the owner instead of patching it silently.

## 1. Classify and announce

Classify **Feature / Bug / Refactor / Investigate**, then pick the shape:

| | Shape | Handling |
|---|---|---|
| **Fast path** | Any one of: (a) doc/comment-only; (b) a non-`.cs`, non-`.ts` file; (c) a single-line `.cs` or `.ts` with no logic, signature, query or DI impact - and no touched file is on the never-list. | Manager edits inline -> `scout-mechanic` build gate (0 warnings; plus `scripts/check-diagnostics.sh <touched-proj>` if any `.cs` changed) -> commit message proposed. No other dispatch. |
| **Full pipeline** | Everything else, however small the diff looks. | Vault, six-part plan, `scout-implementer`, conformance check, every hard gate below. |

- **Never fast path, whatever the extension:** `.editorconfig`, `Directory.Build.props`,
  `Directory.Packages.props`, `global.json`, `.github/workflows/**`, `appsettings*.json`,
  `.claude/**`, `scripts/**`, `ui/package.json`, `ui/bun.lock`, `ui/build/**`.
- **Never trivial:** anything that adds or changes a call leaving the process (an SDK call, an HTTP
  or gRPC call, a filesystem write), a function or tool the LLM can invoke, a plugin operation, a
  control-plane command, credential or token handling, an adapter, an LLM provider, DI registration.

State classification + one-line reason + **skills in play** before the first edit. Skills in play
is one line naming every installed skill that applies and whether you use it, or saying plainly
that none do; skill matching is otherwise silent. Usual candidates beyond `scout-pipeline` and
`ponytail`: `superpowers:brainstorming` and `product-management:write-spec` (discovery for an owner
request with no spec), `wiki-crystallize` / `wiki-lint` (gate 7), the `dotnet-test:*` skills
(`AGENTS.md`), and `stryker-project` when the owner asks whether the tests would catch a bug in a
whole project (on demand, never a gate). Naming one you then skip is fine; skipping it unnamed is
not.

## 1b. Climb the ladder before writing anything

Invoke **`ponytail`** after the vault reading and before the first edit, on every non-trivial lane
(Rule C with a procedure). The order is fixed: **vault first, ladder second, edit third.** Two
reconciliations: Rule B beats ponytail's "one runnable check, no frameworks" (the check here is an
xUnit + Shouldly test in the project's test project), and a documented convention (the adapter,
provider and plugin abstractions, Semantic Kernel as the one LLM path, the descriptive/executive
split) is never over-engineering; if one looks like bloat, that is a superseding ADR, not a shortcut.

## 2. Read the lane file

Read exactly one from this directory: `feature.md` | `bugfix.md` | `refactor.md` | `diagnose.md`.

## Plan shape (state it before dispatching, on every non-trivial lane)

No narrative, no options survey, no restating the request. Exactly six parts; the plan travels
verbatim into the `scout-implementer` dispatch and the conformance check is against it:

1. **Files** - exact `EDIT` and `CONTEXT` paths, including the vault pages the implementer writes
   (project page, ADR amendment, roadmap entry).
2. **Steps** - ordered in Rule B shape, naming the test class each step touches. On the bug and
   refactor lanes step one is the failing or pinning test.
3. **Invariants at risk** - vault pages and the tests that pin them. A step that would violate an
   invariant is not a step: stop, a superseding ADR first.
4. **Acceptance** - literal build, format, test and diagnostics commands.
5. **Out of scope** - adjacent work this change must not touch.
6. **Requirement-to-test map** - every requirement in the user's words, the exact test path and
   test name that prove it, and its kind: `new behaviour` (new or changed behaviour; must fail when
   the behaviour is removed), `behaviour lock` (behaviour-preserving refactor; green before and
   after - red before means reclassify as a feature or fix) or `mutation proof` (a brand-new project
   or slice where nothing compiles without the change). Every test asserts the requirement, never
   registration, mapping or a call happening. The map travels unchanged into gate 5, the review
   dispatch and the PR body.

Two things the plan always states outright: the **write classification** of every call that leaves
the process (descriptive, and to which channel; or none) and whether the change crosses a
**vendor boundary** (which abstraction it goes through). A plan thin enough that implementing it
means re-grepping the repo was stated too fast: fix the plan.

## 3. Hard gates (every non-trivial code lane)

1. **Vault first** - `vault/pages/services/<project>.md` + every invariant it links, BEFORE editing.

   **Capability check.** The project page carries no rules for a capability you are about to *add*:

   | The change... | Read first | The trap |
   |---|---|---|
   | adds or changes a **call that leaves the process** | ADR 0009 + [[pages/invariants/no-executive-writes]] + Rule A | a descriptive write that can be read by an agent which acts on it is still descriptive; a write that mutates anything outside Scout's config is executive and forbidden; boundary cases default to forbidden |
   | adds a **function or tool the LLM can invoke**, a plugin operation or a control-plane command | ADR 0009, ADR 0012, `pages/architecture/plugin-catalogue.md`, `pages/services/control-plane.md` | the model decides when to call it; judge what it can reach, not what its description promises |
   | touches **credentials, tokens, headers, configuration binding** | [[pages/invariants/secrets-never-in-source]] + Rule G | a literal in `appsettings*.json`, a token in a log line or an exception message |
   | adds or changes an **adapter, LLM provider or plugin** | [[pages/invariants/pluggable-boundaries]] + ADR 0012 + Rule F | a vendor SDK type leaking into the core or another adapter; a second LLM path beside Semantic Kernel |
   | adds or changes a call across an **external boundary** (HTTP, gRPC, chat SDK, provider, filesystem, subprocess) | `pages/conventions/testing.md` "One failing-call test per external boundary" + Rule I | the happy path is tested and the timeout, 5xx or reset path is not |
   | changes the **default provider or model** | [[pages/invariants/local-first-default]] + ADR 0008 | a contributor without paid credentials can no longer run Scout |
   | touches **streaming** (conversation sink, adapter delivery) | ADR 0013 | a stream collected before the first chunk reaches the channel; platform edit rate limits |
   | touches **`ui/**`** | ADR 0004, ADR 0011, `pages/architecture/electron-provisioning-ui` | the renderer never talks to the worker except through the gRPC/UDS control plane |

2. **Rule B per edit** - edit -> test -> validate structure -> validate build.
3. **Build, format, diagnostics.** `dotnet build Scout.slnx` at 0 warnings, `dotnet format
   Scout.slnx --verify-no-changes` clean, **and** `scripts/check-diagnostics.sh <touched-proj>`
   clean. Three gates: the build enforces style, but test projects emit no IDE0005 without a
   documentation file and info-level diagnostics never fail a build. One `.csproj` per
   check-diagnostics run. The implementer runs these before reporting; the manager dispatches
   `scout-mechanic` with literal commands when it needs an independent run.
4. **Full test surface green.** `--filter` is allowed for a named subset when a proof or a batch
   needs it; the full surface below then runs once **without** a filter before the commit message.
   Require `N passed` per project and a Skipped count of 0:

   | The diff touches... | The surface is |
   |---|---|
   | one `src/Scout.X` project | `tests/Scout.X.Tests` + the test project of every project that references `Scout.X` (`pages/architecture/project-map.md`) |
   | `src/Scout.ControlPlane.Contracts` or anything three or more projects reference | every test project under `tests/` |
   | `src/Scout.Host.*` or DI registration | the host's test project + every adapter and plugin test project |
   | `ui/**` | in `ui/`: `bun run typecheck`, `bun run lint`, `bun run format`, `bun run test` (per package counts) |
   | `.claude/hooks/**` | every `.claude/hooks/*.test.sh` (`pre-git-guard`, `stop-review-gate`, `session-start`, `post-edit-oracle-note`, `session-end-metrics`; each must print `ALL CASES PASS`; CI runs all five) and a run of any other touched hook |
   | `scripts/check-vault-refs.sh` | `bash scripts/check-vault-refs.test.sh` (`ALL CASES PASS`) and a run on the real vault |
   | `.claude/skills/scout-pipeline/**` | `claude plugin eval .claude/skills/scout-pipeline` passes every case under `evals/`; a lane or SKILL.md edit that changes what a grader asserts updates the eval in the same diff |
   | contract/config/doc text only (`vault/**`, `*.md`, and `.claude/**` paths no row above matches) | no executable surface - record gates 3-4 as N/A and say why |

5. **Prove the requirement.** The implementer proves the map inside its own run and reports the
   evidence; the manager checks it.
   - **`new behaviour`:** for each entry, back up the production file, apply one targeted mutation
     (or revert the rule) that removes exactly the behaviour under test - let a forbidden operation
     through, drop a configuration check, make a fail-closed branch resolve - run the mapped test,
     and quote its failure: the test name and the **assertion** message. A compile failure is never
     proof. Restore the file from the backup copy (never `git checkout`, which wipes uncommitted
     work) and rebuild clean.
   - **`mutation proof`:** the same procedure for a brand-new project or slice; stubbing a whole
     slice proves nothing.
   - **`behaviour lock`:** the named test green before the change and green after it.
   - **`scout-mechanic`** takes the long runs the implementer should not block on (the full
     solution suite, the ui workspace checks).
   - A missing or incomplete result for any entry blocks the PR.

   The implementer also runs `dotnet-test:test-anti-patterns` and `dotnet-test:assertion-quality`
   over every mapped test path and quotes both results in its report (a weak assertion found there
   is fixed before the report, not after the review). The gate output holds the unchanged map, each
   mutation with its quoted failure and restore, the behaviour-lock runs, both skill results, and
   the verdict.
6. **Review gate: one workflow run, the lenses the diff names.** Lenses: `read-only`, `secrets`,
   `boundaries`, `invariants`, `performance`. `invariants` always runs; `performance` means
   dispatching `scout-optimizer` in the same message, mandatory when the diff touches streaming,
   knowledge indexing or retrieval, a hosted service, the gRPC control plane or an LLM provider call
   path. The other three come from this table; the manager may add a lens, never drop one the table
   names:

   | The diff... | adds lens |
   |---|---|
   | adds or changes a call leaving the process, an LLM-invocable function, a plugin operation or a control-plane command | `read-only` |
   | touches configuration binding, tokens, credentials, auth headers, `appsettings*.json` | `secrets` |
   | touches an adapter, a provider, a plugin registration, `Scout.ControlPlane.Contracts`, or any type crossing those abstractions | `boundaries` |

   - **`scout-reviewer`** (opus) - run the saved workflow **once per logical change**:
     `Workflow({ name: "scout-review", args: { module: "<Scout.Project>", lenses: [<the lenses
     above>], dispatch: "<the reviewer template below, filled>" } })`. It dispatches one
     `scout-reviewer` per lens in parallel (`invariants` also owns regression coverage, Rules H and J
     and generic correctness), then has an independent reviewer try to refute every finding, and
     returns `{ confirmed, refuted, checked, uncited }`. The reviewer checks the evidence the
     dispatch carries; it never rebuilds the map or reruns the proofs. If the Workflow tool is
     unavailable, dispatch `scout-reviewer` directly with the same template. **Budget: two
     fix-and-review rounds**; a third means the change is not understood - stop and hand the rest to
     the owner.
   - **`/code-review`** runs only when the diff touches `Scout.ControlPlane.Contracts`, a host, or
     the owner asks; always `medium`, never `--fix`.
   - **Receiving a batch:** before applying any finding, verify it against the code the way
     `superpowers:receiving-code-review` prescribes (reproduce or read the line it cites; a finding
     that does not hold is answered, not applied). Apply the `confirmed` findings, record the
     `refuted` ones with the reason in the dispatch report, then re-gate.
7. **Crystallize.** The implementer has written the vault pages the plan named; the manager reviews
   that vault diff, owns `index.md`, `log.md`, the conventions and process ADRs, and uses
   `wiki-crystallize` for anything the session found beyond the plan. Vault edits land in the same
   commit as the code they describe. **7b.** Run `wiki-lint` when the vault changed; it does not
   check `file:line` drift, so also run `bash scripts/check-vault-refs.sh` on every lane that touched
   `src/`, `ui/` or `vault/` and fix every ref it reports (what it checks and skips:
   `vault/pages/conventions/tooling.md`). Then read every ref you rewrote. A page over 300 lines
   that is not a tracker fails the gate; the lint reports it as an over-length page.
8. **The PR body** uses `.github/PULL_REQUEST_TEMPLATE.md`, ticks the read-only invariant box only
   after the `read-only` lens (or the plan's write classification) says so, and carries the line
   `Review: applied N, rejected M` (findings of gate 6, after verification).
9. **STOP at the commit message** - one conventional commit message in a code block. Rule E: never
   commit unless the owner asked; when asked, commits go on a `feat/ fix/ chore/ docs/` branch and
   land on `main` only through a PR; never an AI attribution line in a commit or a PR.

## Dispatch templates

Implementer dispatch (copy, fill, send verbatim):

```text
Project: <Scout.X>   Worktree: <path>   Lane: feature | bugfix | refactor
1. Files      EDIT: <paths, incl. vault pages>   CONTEXT: <paths>
2. Steps      <ordered, Rule B shape, test class per step>
3. Invariants <vault page -> pinning test>
   Write classification: <none | descriptive to <channel>>   Vendor boundary: <none | via <abstraction>>
4. Acceptance <literal commands>
5. Out of scope <...>
6. Map        <requirement (user's words)> -> <test path::name> [new behaviour | behaviour lock | mutation proof]
Report: files + reason, map with landed test, gate output verbatim, per entry the mutation, quoted
failure, restore and rebuild (or green before/after), departures from the plan, open questions.
```

Reviewer dispatch (the `dispatch` arg of the `scout-review` workflow):

```text
Project: <Scout.X>   Diff: working tree of <worktree> (or <range>)
Lenses: invariants[, read-only][, secrets][, boundaries]   (from the gate 6 table; name the row)
Map: <entries with kind>
Gate 5 evidence: <per entry: mutation, quoted failure, restore; behaviour-lock runs>
Skill results (from the implementer's report): test-anti-patterns <...>; assertion-quality <...>
Check this evidence; do not rebuild it. Findings: CONFIRMED with file:line and the rule or page.
```

## Token rules

- The cheapest tokens are the ones a smaller diff never spends - step 1b is the biggest lever.
- Don't re-run a gate on an unchanged diff; reuse session findings.
- Surveys past a handful of targeted reads go to `Explore`. Long runs go to `scout-mechanic` with
  literal commands; it never fixes.
- The manager edits only vault, docs, `.claude/**` and fast-path one-liners; simple well-specified
  changes go to a `sonnet` agent instead of `scout-implementer`.
