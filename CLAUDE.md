@AGENTS.md

# Claude Code in this repo

Everything above holds here. What follows is how this repo uses Claude Code.

## Before you edit code

Use the `scout-pipeline` skill first, for every code task. It classifies the task by risk and
says what the change needs: a one-line edit needs a build, and a change to what Scout can write
or reach needs a plan, proof and a review.

## What loads without being asked

- **The rules of a project** load when you open a file of that project:
  `.claude/rules/<project>.md`. Each holds the rules the project must keep and a table from what
  a change touches to the one page to read. Read that page before the source, not the whole folder.
- **The contract of the documentation folder** loads when you open a file under `context/`.
- **The guide to the installed .NET skills** loads when you open a C# file.
- **The tools for the desktop app** (Angular, Electron) load when you open a file under `ui/`.

For where a type lives or who calls it, ask the code graph before reading files:

| Question | Command |
|---|---|
| What is this type and what does it touch | `graphify explain "<Type>"` |
| What breaks if it changes | `graphify affected "<Type>" --depth 1` |
| How two things connect | `graphify path "<A>" "<B>"` |
| Which types everything hangs on | `graphify god-nodes --top 10` |

Start `affected` at depth 1: depth 2 returns about five times as much. When a name exists in two
files the command lists them; repeat it as `"<path>::<Type>"`. If the graph is missing or stale,
`graphify update . --no-cluster` rebuilds it in about 20 seconds.

## What a hook already enforces

You do not need to remember these; a hook stops or flags them.

| Enforced | By |
|---|---|
| History-writing git, a write to GitHub, a deploy: asks first | `pre-git-guard` |
| A test run outside the container: denied, with the command to use | `pre-test-in-docker` |
| A reviewing agent's shell: read-only git | `agent-read-only-bash` |
| Code edited and no review ran: blocks the reply once | `stop-review-gate` |

## Working with other sessions

- Start a session with `tools/dev/claude-scout.sh` (`worker`, or `ui` for the desktop app): it
  leaves out the servers and plugins this repo does not use.

- One git worktree per piece of work running at the same time. An agent never works in the
  owner's own checkout.
- When a session found something worth keeping, run `/wiki-crystallize` and add one line at the
  top of `context/log.md`.
- The owner's private notes are outside this repository. Nothing from them is copied here without
  the owner's word: this repository is public.
