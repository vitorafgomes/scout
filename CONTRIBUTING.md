# Contributing to Scout

Thanks for your interest in Scout.

## Ground rules

Scout is a **read-only** assistant. No contribution may introduce write
operations against external systems (no issue creation, no posting outside
the originating chat, no git writes, no cloud mutations). This is a security
invariant, not a preference — PRs that break it will not be merged.

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

## Architectural changes

Any change that touches:

- `Scout.Core` contracts
- The plugin registry or capability descriptors
- The LLM provider abstraction
- The chat adapter abstraction

requires a short ADR under `vault/pages/decisions/` (copy
`vault/templates/_adr.md`, next number). For external contributors, describing
the change in the PR body is enough; a maintainer will port it to an ADR on merge.

The invariants under `vault/pages/invariants/` are the rules every PR is reviewed
against; read `vault/pages/invariants/no-executive-writes.md` before adding any
call that leaves the process.

## DCO / CLA

No CLA. Contributions are accepted under the project's MIT license. By opening
a PR, you confirm you have the right to contribute the code and that it can
be released under MIT.

## Questions

Open a GitHub Discussion or an issue tagged `question`.
