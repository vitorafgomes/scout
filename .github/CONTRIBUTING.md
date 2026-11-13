# Contributing to Scout

Thanks for your interest in Scout.

## Ground rules

Every write Scout makes is **declared, granted and approved**. This is the
write contract, a security invariant, not a preference; PRs that break it will
not be merged:

- A feature declares every call that leaves the process and whether it reads,
  writes, or writes irreversibly. A write it does not declare is a bug.
- A write-capable feature runs only after the owner grants it in the desktop
  app, and an irreversible action (publishing, deleting, approving a pull
  request) runs only after the owner approves that one action there.
- Every write is logged. A chat message, a prompt or the model's output can
  ask for a write, never grant or approve one.
- Scout never writes or edits code, and never writes on the host outside its
  own folders. A project's tests run only inside the isolated, disposable
  container the owner enables.

`context/pages/decisions/0032-per-feature-write-permissions.md` is the decision;
`context/pages/invariants/no-executive-writes.md` is how the code enforces it.

## Getting set up

Requirements:

- .NET 10 SDK
- Docker (optional, for running Ollama locally)

```bash
git clone https://github.com/vitorafgomes/scout.git
cd scout
dotnet restore
dotnet build
dotnet test
```

## Workflow

1. Open an issue describing the change before starting work on anything
   non-trivial. For small fixes (typos, obvious bugs), a PR is fine without
   a prior issue.
2. Fork and create a branch: `feat/<slug>`, `fix/<slug>`, or `chore/<slug>`.
3. Keep commits atomic, one concern per commit.
4. Run `dotnet format` and `dotnet test` before pushing.
5. Open a PR against `main`. Fill in the template.

## Code style

- All code, identifiers, and comments in English.
- No emojis in source files.
- No AI-attribution comments.
- Follow the `.editorconfig` in the repo; Rider/VS Code pick it up automatically.
- Secrets: use User Secrets in development, environment variables in production.
  Never commit secrets.

## Tests

- Unit tests are required for new code that contains logic.
- Integration tests are welcome but not required by default.
- Tests run under `dotnet test` with no extra setup.
- The end-to-end tests of `Scout.Sandbox` (trait `Category=Docker`) need a running Docker daemon; `tools/checks/test-per-project.sh` leaves them out, and says so, when none answers.

## Architectural changes

Any change that touches:

- The control-plane contracts (`src/Scout.ControlPlane.Contracts`, the gRPC protos)
- The feature catalog and its declared reads and writes
  (`src/Scout.ControlPlane/Features/FeatureCatalog.cs`)
- The LLM provider abstraction (`src/Scout.Llm/Abstractions/`)
- The chat adapter abstraction (`src/Scout.Conversation/Abstractions/IChatAdapter.cs`)

requires a short ADR under `context/pages/decisions/` (copy
`context/templates/adr.md`, next number). For external contributors, describing
the change in the PR body is enough; a maintainer will port it to an ADR on merge.

The invariants under `context/pages/invariants/` are the rules every PR is reviewed
against; read `context/pages/invariants/no-executive-writes.md` before adding any
call that leaves the process.

## DCO / CLA

No CLA. Contributions are accepted under the project's MIT license. By opening
a PR, you confirm you have the right to contribute the code and that it can
be released under MIT.

## Questions

Open a GitHub Discussion or an issue tagged `question`.
