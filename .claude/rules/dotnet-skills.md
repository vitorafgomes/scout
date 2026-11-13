---
paths:
  - "**/*.cs"
  - "**/*.csproj"
  - "Directory.*.props"
---

# .NET skills for this repo

The `dotnet/skills` plugins (marketplace `dotnet-agent-skills`) say how to do a .NET task well.
When one covers the task, use it by name. A plugin may not be installed on this machine: check
with `jq -r '.plugins | keys[] | select(test("dotnet"))' ~/.claude/plugins/installed_plugins.json`,
and install a missing one with `/plugin install <plugin>@dotnet-agent-skills`.

| Task | Skill or agent |
|---|---|
| Find, rename or diagnose a C# symbol across `Scout.slnx` | The C# language server the `dotnet` plugin registers |
| Run or filter tests | `dotnet-test:run-tests`, through `tools/checks/in-docker.sh` |
| Where to add tests | `dotnet-test:coverage-analysis`, `dotnet-test:test-gap-analysis` |
| Weak or missing assertions, test smells | `dotnet-test:test-anti-patterns`, `dotnet-test:assertion-quality`, `dotnet-test:test-smell-detection`, `dotnet-test:grade-tests` |
| Risk of complex code under test | `dotnet-test:crap-score` |
| Health of a whole test suite | Agent `dotnet-test:test-quality-auditor` |
| Make code testable (`TimeProvider`, `IFileSystem`) | Agent `dotnet-test:testability-migration` |
| Allocations, async traps, a hot path | `dotnet-diag:analyzing-dotnet-performance`, `dotnet-diag:microbenchmarking`, agent `dotnet-diag:optimizing-dotnet-performance` |
| A hang, a leak or a crash | `dotnet-diag:dotnet-trace-collect`, `dotnet-diag:dump-collect` |
| Mutation-test one project | `stryker-project` (this repo's skill): `dotnet tool restore`, then `dotnet tool run dotnet-stryker` |
| Pin or install a .NET SDK locally | `dotnet:setup-local-sdk` |
| OpenTelemetry wiring | `dotnet-aspnetcore:configuring-opentelemetry-dotnet` (not installed by default) |
| An AI, LLM or retrieval feature in .NET | `dotnet-ai:*` (not installed by default) |
| Data access with EF Core (the stores use it on SQLite) | `dotnet-data:*` (not installed by default) |
| A .NET upgrade or an AOT check | `dotnet-upgrade:*` (not installed by default) |
| A build that fails without saying why | `dotnet-msbuild:binlog-generation`, `dotnet-msbuild:binlog-failure-analysis` |

Three things to know:

- **The `dotnet-test:code-testing-*` agents get no working tools in Claude Code**, because their
  definitions list tools in lowercase. Use the matching skill inline.
- **Semantic Kernel stays the only path to the model**, whatever a `dotnet-ai` skill suggests.
- **Not for this repo:** `dotnet-blazor`, `dotnet-maui` (the desktop app is Electron and Angular),
  `dotnet-nuget:convert-to-cpm` (central package management is already in place).
