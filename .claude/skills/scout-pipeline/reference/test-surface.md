# Which tests a change must run

Find the first row the diff matches. Every test command goes through
`tools/checks/in-docker.sh`. Each project reports its own count, and skipped is 0.

| The diff touches | Run |
|---|---|
| One `src/Scout.X` project | `tests/Scout.X.Tests` and the test project of every project that references `Scout.X` (`context/pages/architecture/project-map.md`) |
| `src/Scout.ControlPlane.Contracts`, or anything three or more projects reference | Every test project under `tests/`; and the `ui/**` row when the desktop consumes the changed contract |
| `src/Scout.Host.*` or registration in the container | The host's test project and every adapter and plugin test project |
| `ui/**` | In `ui/`: `bun run typecheck`, `bun run lint`, `bun run format`, `bun run test`, with counts per package |
| `.claude/hooks/**` | Every `.claude/hooks/*.test.sh`; each prints `ALL CASES PASS` |
| `.claude/workflows/scout-review.js` | `.claude/workflows/scout-review.test.sh` |
| `tools/**` | The script's own `.test.sh`, and one run of the script on the real tree |
| `.editorconfig`, `Directory.Build.props`, `Directory.Packages.props`, `Scout.slnx`, anything under `tests/` | `dotnet test tests/Scout.ArchTests`, on top of the row the code change matches |
| `.claude/skills/scout-pipeline/**` | `dotnet test tests/Scout.ArchTests`, which runs `PipelineEvalGraderTests`; a change to what a grader asserts updates the eval in the same diff. The live `claude plugin eval` is advisory: run it when a lane or `SKILL.md` changes wording, and record its result in the PR body |
| Only text: `context/**`, `*.md`, and `.claude/**` paths no row above matches | Nothing executable. Say so, and why |

The Docker end-to-end tests of `Scout.Sandbox` need a Docker daemon, which the container does not
have. `test-per-project.sh` leaves them out and says so; CI runs them in a step of their own.
