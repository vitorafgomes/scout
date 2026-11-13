# tools

The project's scripts, grouped by what they are for. CI, the hooks and the pipeline call them from
here.

## Layout

| Folder | Script | What it does | Who runs it |
|---|---|---|---|
| `checks/` | `in-docker.sh` | Runs a command in the project's container instead of on this machine | Every agent, for every test run |
| | `check-diagnostics.sh` | Fails on any compiler or analyzer diagnostic in a build, at any severity | CI, the implementer |
| | `check-context-refs.sh` | Resolves every `file:line` citation in the context pages | CI, after a change to pages or code |
| | `check-hidden-text.sh` | Finds text a person cannot see but a model reads | CI; by hand on a file from outside |
| | `check-doc-sizes.sh` | Fails when a document a model reads passes its ceiling, or when a listed debt grows (`doc-size-baseline.txt`) | CI |
| | `test-per-project.sh` | Runs each test project on its own and fails on a skipped or empty one | CI, the mechanic |
| `docker/` | `Dockerfile` | The image `in-docker.sh` builds: the .NET SDK, Bun, Node, git, jq and perl, each base pinned | `in-docker.sh`, on first use |
| `release/` | `next-version.sh` | Computes the next version from the commits since the last tag | The Release workflow |
| | `with-test-version.sh` | Gives a local test build its own version, then restores `ui/package.json` | `bun run dist:linux:test` |
| | `release-workflow.test.sh` | Checks the Release workflow file itself | CI |
| `dev/` | `token-usage.sh` | Tokens a Claude Code session used, per agent, and the size of every document a model reads | A person, the `scout-meter` agent |
| | `claude-scout.sh` | Opens Claude Code for this repo with only the servers and plugins the work needs: `worker` (default) or `ui` | A person, to start a session |
| | `install-git-hooks.sh`, `git-hooks/` | Installs the git hooks that refresh the code graph | A person, once per clone |

A script and its self-test sit side by side: `<name>.sh` and `<name>.test.sh`.

## Tests run in the container

An agent never runs the project's tests on this machine. It runs them through `in-docker.sh`:

```
tools/checks/in-docker.sh dotnet test tests/Scout.Llm.Tests
tools/checks/in-docker.sh bash tools/checks/test-per-project.sh
cd ui && ../tools/checks/in-docker.sh bun run test
```

What the container can reach:

| | |
|---|---|
| Files of this machine | The repository only, at the same path, so a path in an error is the path on disk |
| Its own files | A Docker volume, `scout-dev-home`, that holds the package caches between runs |
| User | The calling user, never root; every Linux capability dropped; no new privileges |
| Network | On, because a restore needs it. `SCOUT_OFFLINE=1` turns it off for a run that needs none |
| Docker itself | Not reachable: the Docker socket is never mounted |

Two things enforce it. The hook `.claude/hooks/pre-test-in-docker.sh` denies a test command that is
not routed through `in-docker.sh`, and its reason names the command to run instead. And
`in-docker.sh` has no fallback: when Docker does not answer it runs nothing and exits 2.

What is not covered:

- **The Docker end-to-end tests of `Scout.Sandbox`.** They need a Docker daemon, and the container
  has none on purpose. `test-per-project.sh` leaves them out and says so; CI runs them in a step
  of its own.
- **A person at a terminal.** The hook acts on what an agent runs. Nothing stops `dotnet test`
  typed by hand, and CI on the GitHub runners is unchanged.
- **The build and the format check** are not denied on the host, only tests are. They can go
  through `in-docker.sh` as well.
- **The SDK differs.** The image carries SDK 10.0.203, the band CI builds with; this machine has 10.0.115. Both satisfy
  `global.json`, and both write to the same `bin/` and `obj/`, so a build on one side is followed
  by a rebuild on the other.
