# Scout

An open-source (MIT) chatops assistant for Discord, Slack and Teams. It answers questions by
reading source control, repositories and cloud providers, and it reports problems as issues and
reviews pull requests. It never writes or edits code. .NET 10 worker under `src/`, desktop app
under `ui/` (Bun, Electron, Angular), one test project per project under `tests/`.

## Commands

Tests run in the project's container, never directly on the machine.

| To | Run |
|---|---|
| Build | `dotnet build Scout.slnx` (a warning is an error) |
| Check formatting | `dotnet format Scout.slnx --verify-no-changes` (needs the 10.0.2xx SDK) |
| Check diagnostics of one project | `tools/checks/check-diagnostics.sh <path to .csproj>` |
| Test one project | `tools/checks/in-docker.sh dotnet test tests/<Project>.Tests` |
| Test one test | `tools/checks/in-docker.sh dotnet test tests/<Project>.Tests --filter "FullyQualifiedName~<Name>"` |
| Test everything | `tools/checks/in-docker.sh bash tools/checks/test-per-project.sh` |
| Architecture rules | `tools/checks/in-docker.sh dotnet test tests/Scout.ArchTests` |
| Desktop app, in `ui/` | `bun run typecheck`, `bun run lint`, `bun run format`, `../tools/checks/in-docker.sh bun run test` |

A change is done when the build has no warning, formatting and diagnostics are clean, and every
test project the change reaches is green with nothing skipped.

## Rules

**Every write is declared, granted and approved.** A feature declares each call that leaves the
process and whether it writes. A write runs only after the owner grants that feature in the
desktop app, an irreversible one only after the owner approves that action there, and every write
is logged. The model and the chat can only ask. A write nobody declared, or one outside Scout's
own folders, is forbidden. A function the model can invoke is judged by what it can reach, never
by what its description promises.

**A change ships with its test.** The test fails without the change and asserts the requirement
in the words of the request. A refactor names tests that are green before and after.

**One test where the call fails.** A change that adds or alters a call across HTTP, gRPC, a chat
SDK, a provider, the filesystem or a subprocess ships a test in which that call fails (timeout,
5xx, reset) and asserts what the caller does.

**Adapters, providers and plugins are swapped by configuration.** A vendor SDK type stays inside
its own adapter, provider or plugin. The model is reached through Semantic Kernel only.

**Credentials come from configuration binding.** User Secrets in development, environment
variables in production. `appsettings*.json` holds names and no values. Nothing logs a credential.

**Tests assert with Shouldly.** FluentAssertions is commercially licensed from version 8 and is
never added.

**No abstraction for one user.** A generic, a base class or a helper needs a second caller.
Adapters, providers and plugins are documented abstractions and stay.

**Read before you cite.** A file, class or convention is read in the current tree before it is
recommended.

**Documentation lives in `context/`, in English, with no emojis.** Code carries a `<summary>` on a public type or
member, and no comment that narrates inside a member or refers to a decision, a page or an issue.
Identifiers and comments are in English.

**Nothing is taken from another project,** whatever its licence. An idea from elsewhere is
recorded in `context/` by what it does, and third-party files are listed in
`THIRD-PARTY-NOTICES.md`.

**Git belongs to the owner.** No commit, push, pull request, release or merge unless the owner
asks for that action. Every change reaches `main` through a pull request. A commit, a pull
request and a source file never carry a line that credits an AI tool.

## Where things are

- Why something is the way it is: `context/pages/decisions/`.
- Rules that span projects: `context/pages/invariants/`.
- How to release and operate: `context/pages/runbooks/`.
- The tools above, and what each one checks: `context/pages/conventions/tooling.md`.
