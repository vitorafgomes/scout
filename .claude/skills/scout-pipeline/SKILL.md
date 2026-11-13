---
name: scout-pipeline
description: Use when a request in this repo would write or edit code - a fix ("fix", "broken", "error", "doesn't work"), a feature ("add", "implement", "create", "new adapter", "new plugin", "new provider"), a refactor ("clean up", "restructure", "rename", "extract") or a diagnosis ("why", "investigate", "debug") - even as a plain one-line request, and when it is unclear whether code is being asked for.
---

# Scout task pipeline

From request to a proposed commit message. The size of the process follows the risk of the change.

The rules: `CLAUDE.md`, `.claude/rules/<project>.md`. This file: the order of work.

## 1. Look at the tree, then classify

Run `git status --porcelain` and `git diff HEAD --stat` and classify from what they list.

Say the kind (**Feature**, **Bug**, **Refactor**, **Investigate**), the size from the table below
with the row that decided it, and each installed skill that applies and whether you use it.

| Size | The change | What runs |
|---|---|---|
| **Fast path** | Only comments or documentation; or only an inert file (`.md`, `.txt`, a raster image); or one line of `.cs` or `.ts` with no effect on logic, a signature, a query or registration. | You edit. Build and diagnostics for the touched project. No agent. |
| **Standard** | Anything that is neither fast path nor sensitive. | A plan. You implement when the plan edits files of one project; `scout-implementer` when it edits two or more. Checks, proof, and one review with the `invariants` lens. |
| **Sensitive** | Any row of `reference/sensitive.md` matches. | A plan, `scout-implementer`, checks, proof, a review with every lens that file names, `scout-optimizer` when it names it. |

Two rows override the first: a file on the never-fast-path list in `reference/sensitive.md` is at
least Standard, and a match in its table is Sensitive however small the diff.

Which skills of the superpowers plugin to use here, and which to leave out: `reference/superpowers.md`.

## 2. Read the lane

Exactly one file of this folder: `feature.md`, `bugfix.md`, `refactor.md` or `diagnose.md`.

## 3. Plan (Standard and Sensitive)

Six parts, no narrative. It goes word for word to whoever implements, and the result is checked
against it line by line.

1. **Files**: paths to edit and paths to read. The edit list includes every page of
   `reference/records.md` that applies: the history page, the topic page, the testing page.
2. **Steps**: in order, each naming its test class. For a bug or a refactor, step one is the
   failing or pinning test.
3. **Rules at risk**: the lines of the rules file the change could break, and the test that pins
   each. A step that would break one is not a step: a superseding decision comes first.
4. **Acceptance**: the literal commands, from `reference/test-surface.md`.
5. **Out of scope**: nearby work this change leaves alone.
6. **Requirement table**: each requirement in the user's words, the test path and name that prove
   it, and its kind. Add a row for each behaviour the change implements beyond the request: a
   limit, a reset, a failure path. A request to build what an artboard draws follows
   `reference/prototype.md`: one row per thing the artboard draws.

| Kind | Use it for | The proof |
|---|---|---|
| `new behaviour` | New or changed behaviour | The test fails when the behaviour is removed |
| `behaviour lock` | A refactor that keeps behaviour | The test is green before and after |
| `mutation proof` | A new project or slice, where nothing compiles without the change | The test fails on one targeted mutation |

The plan also states, for every call that leaves the process, what its feature declares (read,
write, irreversible write), and which abstraction a vendor type stays behind. A plan that leaves
whoever implements searching the repo was stated too fast: name the files and fix the plan.

Before the first edit, use the `ponytail` skill to find the smallest change. It does not replace
the xUnit and Shouldly test, nor a documented abstraction (adapter, provider, plugin).

## 4. Build

Edit, then the test for that edit, then the architecture tests, step by step. When the
result departs from the plan, tell the owner; do not patch it over.

Tests run in the container: `tools/checks/in-docker.sh <command>`. Dispatch forms for the
implementer and the reviewer are in `reference/dispatch.md`.

## 5. Check

- `dotnet build Scout.slnx` with 0 warnings.
- `dotnet format Scout.slnx --verify-no-changes`, clean.
- `tools/checks/check-diagnostics.sh <touched .csproj>`, clean, one project per run.
- The test surface from `reference/test-surface.md`, through the container, with a count per
  project and 0 skipped. A filter is for one named proof; the full surface then runs once without
  one. A change to `Scout.ControlPlane.Contracts`, or to anything three or more projects reference,
  runs every test project under `tests/`, and the `ui/` checks when the desktop reads the contract.

A long run starts in the background:
`tools/checks/in-docker.sh bash tools/checks/test-per-project.sh`.

## 6. Prove (Standard and Sensitive)

For each row of the requirement table, whoever implemented records the proof its kind asks for:
the mutation with the test's assertion message quoted and the file restored from a backup copy,
or the green runs before and after. A row without proof blocks the commit message.

## 7. Review (Standard and Sensitive)

One run of the `scout-review` workflow with the lenses for the size. It sends one
`scout-reviewer` per lens, then has an independent reviewer try to refute each finding.

| The workflow returns | You do |
|---|---|
| `confirmed` | Read the line it cites, apply it, run the checks again, and send the new diff to review |
| `unresolved` (the refuter returned nothing, or could not find it) | Verify it yourself; it blocks like a confirmed one until you have |
| `refuted` | Record it with the reason |
| `unresolvedLenses` | Run that lens again; it blocks until it returns |

Two rounds of fix and review. A third means the change is not understood: stop and hand the rest
to the owner.

## 8. Record, then stop

- Context pages change in the same commit as the code they describe. `reference/records.md`
  lists what a change records and where; every row that applies is written.
- The repo's own skills do the page work:

  | When | Skill |
  |---|---|
  | The session found something no page says | `wiki-crystallize` |
  | A page was created | `wiki-integrate`, for the index and the links back |
  | Pages changed | `wiki-lint`, then `tools/checks/check-context-refs.sh`; read every citation you rewrote |
  | The owner asks whether the tests of a project would catch a bug | `stryker-project` |
- The size check and the pull request body: `reference/records.md`.
- Propose one conventional commit message in a code block, and stop. The owner decides about git;
  a commit the owner asks for goes on a `feat/`, `fix/`, `chore/` or `docs/` branch.
