# CLAUDE.md - Scout

Loaded into every session in this repo, for me, for every subagent I spawn, and for anyone pairing with Claude here. One source per rule: this file carries the snapshot, the standard, the ten team rules and pointers. It never restates what the pipeline skill, the tooling page or the vault already say.

## Read first

- **Vault:** [`vault/index.md`](./vault/index.md), then `vault/pages/services/<project>.md`, the invariants it links, the relevant ADRs. Vault first, source last. Operation contract: @vault/CLAUDE.md
- **Pipeline:** every code task, however small, routes through the `scout-pipeline` skill before the first edit. It classifies the task, takes the fast path for trivial edits, and walks the gates (plan, Rule B, build, format and diagnostics, test surface, prove the requirement, review, crystallize, PR body, stop at the commit message).
- **Who does what, on which model, with which tools:** `vault/pages/conventions/tooling.md` (routing table, agents under `.claude/agents/`, hooks, scripts, the review workflow).
- **.NET mechanics:** [`AGENTS.md`](./AGENTS.md) maps tasks to the installed `dotnet/skills` plugins.

## Project snapshot

- **What:** open-source (MIT), read-only chatops assistant for Discord, Slack and Teams. It answers operational and business questions by reading from source control, code repositories and cloud providers. It never writes.
- **Stack:** .NET 10 (`global.json` pins the SDK), Microsoft Semantic Kernel as the one LLM path (ADR 0012), `Discord.Net`, `SlackNet`, Bot Framework for Teams, OpenTelemetry. Solution file `Scout.slnx`.
- **Projects:** `src/Scout.Adapter.{Discord,Slack,Teams}`, `Scout.Conversation`, `Scout.Skills`, `Scout.Llm`, `Scout.Knowledge`, `Scout.Plugin.GitHub`, `Scout.ControlPlane` + `.Contracts`, hosts `Scout.Host.Console` and `Scout.Host.ControlPlane`; one test project per project under `tests/`. `src/old` and `tests/old` are the retired first cut; nothing new goes there. `vault/pages/architecture/project-map.md`.
- **Desktop UI:** `ui/` (Bun, Electron 45, Angular 22) provisions chat clients and spawns and supervises the `Scout.Host.Console` worker directly; the gRPC/UDS control plane (ADR 0004, ADR 0011) exists on the .NET side only, the Electron client is the open initiative in `vault/pages/roadmap/control-plane-client.md`. Same repo, same vault. `vault/pages/services/desktop-ui.md`.
- **LLM providers:** pluggable; Ollama local is the default so a contributor runs Scout without paid credentials (ADR 0008); OpenRouter, Azure OpenAI and OpenAI behind the same abstraction.
- **Secrets:** User Secrets in development, environment variables in production; `appsettings*.json` ships without values.
- **Tests:** xUnit + Shouldly + NSubstitute, `dotnet test` with no extra setup; `ui/` tests through `bun run test`. Build at 0 warnings with `TreatWarningsAsErrors` and `EnforceCodeStyleInBuild`.
- **CI:** `.github/workflows/ci.yml` (hook self-tests, restore, build, test) and `ui-ci.yml`.

## The standard

Correct, tested, clean, and **read-only by contract**. The one thing that is never "harden later" is the descriptive/executive write split (ADR 0009): Scout may produce content a human or another agent reads and decides on, and may never mutate code, infrastructure, configuration, data or the host filesystem outside its own config. A boundary case defaults to forbidden. Because the model decides when to call a function, a new LLM-invocable function is judged by what it can reach, never by what its description promises.

## Team rules

The letters are stable; hooks, skills and the vault reference them by letter. A to E carry the same meaning as in the owner's other repos; F to J are this repo's.

- **A - Read-only.** Every call that leaves the process is at most a descriptive write, posted in the originating channel; never an executive write. `vault/pages/invariants/no-executive-writes.md`, ADR 0009.
- **B - Edit, test, validate, per edit.** Every edit ships with a test that fails without it and asserts the requirement in the words the request used; a refactor names behaviour-lock tests green before and after. Then structure, then `dotnet build Scout.slnx` at 0 warnings, `dotnet format Scout.slnx --verify-no-changes` clean **and** `scripts/check-diagnostics.sh <touched.csproj>` clean. `vault/pages/conventions/testing.md`.
- **C - No premature abstraction.** One concrete user means no generic, base class or helper; three similar lines beat a speculative abstraction. A documented abstraction (adapter, provider, plugin) is not premature.
- **D - Verify before citing.** Read the file, class or convention before recommending it; nothing here is stable enough to quote from memory.
- **E - Claude never commits by default.** No history-writing git, no `gh pr`, no release, no publish, unless the owner asks for that action; a batch authorisation in words covers one session's commits and PRs on a `feat/ fix/ chore/ docs/` branch and never a push to `main`. Every change lands on `main` through a PR. **Never an AI attribution line** (`Co-Authored-By`, "Generated with") in a commit, a PR or a source file. The `pre-git-guard` hook turns these into a permission ask.
- **F - Pluggable boundaries.** Chat adapters, LLM providers and plugins are swapped by configuration; a vendor SDK type never crosses into `Scout.Conversation`, `Scout.Skills`, `Scout.ControlPlane.Contracts` or another adapter; the LLM is reached through Semantic Kernel only. `vault/pages/invariants/pluggable-boundaries.md`, ADR 0012.
- **G - Secrets never in source.** Credentials arrive through configuration binding only (User Secrets in dev, env vars in prod); `appsettings*.json` carries names, never values; nothing logs or echoes a credential. `vault/pages/invariants/secrets-never-in-source.md`.
- **H - Shouldly, never FluentAssertions.** Every test project; FluentAssertions v8+ is commercially licensed. ADR 0003.
- **I - One failing-call test per external boundary.** A change that adds or alters a call across HTTP, gRPC, a chat SDK, a provider, the filesystem or a subprocess ships one test where that call fails (timeout, 5xx, reset) and asserts what the caller does. `vault/pages/conventions/testing.md`.
- **J - Documentation lives in the vault.** English only, no emojis, no AI-attribution comments or headers. A `<summary>` on a public type or member is fine; no narration inside a member, no reference to an ADR, a page or an issue in code; the one exception is a `ponytail:` deferral line.

Also mandatory: identifiers and comments in English; the first cut under `src/old` and `tests/old` is read for history and never extended.

## When working here

- Announce classification and the skills in play before the first edit; the `scout-pipeline` skill says how.
- Never touch the owner's checkout (`~/WorkSpace/scout`) from an agent: one git worktree per concurrent piece of work.
- After a session with new findings: `/wiki-crystallize`, and one line at the top of `vault/log.md`.
- Prefer editing an existing file over creating one; no planning or analysis markdown outside the vault. The owner's private notes stay in the Obsidian vault at `~/WorkSpace/obsidian-vault/scout/`; nothing from there is copied here without the owner's word, because this repo is public.
