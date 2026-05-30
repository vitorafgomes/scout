---
name: production-readiness-checks
description: Use before marking features complete, opening PRs, or finishing GSD phases. Applies cross-cutting quality checks that standard unit tests miss - failure/fault injection, concurrency (race conditions, deadlocks), resilience (timeouts, retries, circuit breakers), memory leaks, observability, and security basics. Delegates to per-category playbooks under playbooks/.
---

<objective>
Run a pragmatic checklist of production-readiness concerns on the current change set. Goal: catch classes of bugs that do not appear in happy-path unit tests but cause real incidents (race conditions, missing timeouts, unhandled upstream failures, resource leaks, silent errors).

Not every check applies to every feature. Decide based on what the feature actually touches.
</objective>

<when_to_use>
Invoke this skill when:
- Finishing a feature, about to open a PR, or finishing a GSD phase
- Reviewing a peer's code that crosses an external boundary (HTTP, DB, queue, cache, filesystem)
- Auditing an existing service suspected of hidden reliability issues
- User explicitly asks: "production ready?", "what are we missing?", "quality gates", "teste de falha", "race condition", "memory leak"

Skip when:
- Pure refactor with no behavioural change and full test coverage
- UI-only change with no external call
- Typo / rename / import fix
</when_to_use>

<decision_matrix>
Decide which playbooks apply by scanning what the change touches:

| The change involves...                                | Apply playbook                                    |
|-------------------------------------------------------|---------------------------------------------------|
| Call to external system (HTTP, DB, queue, cache, fs)  | failure-testing, resilience                       |
| Shared mutable state (static, singleton, cache)       | concurrency                                       |
| async/await, Task.Run, goroutines, threads            | concurrency                                       |
| Long-lived process, background worker, event handler  | memory-leaks                                      |
| New endpoint, handler, or public surface              | observability, security-basics                    |
| Serialization, DTO, round-trip transform              | (property-based round-trip — see concurrency doc) |
| Money, counters, inventory, any numeric invariant     | concurrency (PBT invariants)                      |

If two or more rows match, run those playbooks in parallel (spawn agents concurrently when possible).
</decision_matrix>

<process>
1. **Scope**: list files changed since branch diverged. Summarise in 1 line what the change does.
2. **Match**: use the decision matrix above to pick applicable playbooks. State explicitly which ones apply and which are skipped (and why skipped).
3. **Run**: for each applicable playbook, read it from `playbooks/<name>.md` and execute its checklist against the change. Record findings.
4. **Report**: produce a single consolidated report with:
   - **Blockers** — must fix before merge (real bug or missing critical safeguard)
   - **Warnings** — should fix but not blocking (missing observability, weak test)
   - **Notes** — informational (pattern to watch, suggested follow-up)
5. **Do not** invent problems. If a playbook finds nothing, say "clean" and move on. Signal-to-noise matters.
</process>

<available_playbooks>
- `playbooks/failure-testing.md` — fault injection at external boundaries (start here for any integration)
- `playbooks/concurrency.md` — race conditions, deadlocks, property-based parallel tests (planned)
- `playbooks/resilience.md` — timeouts, retries, circuit breakers, idempotency (planned)
- `playbooks/memory-leaks.md` — leak patterns per stack (planned)
- `playbooks/observability.md` — correlation IDs, metrics, log hygiene (planned)
- `playbooks/security-basics.md` — secrets, injection, headers (planned)

Missing playbooks should be created when first needed, not upfront.
</available_playbooks>

<golden_rule>
Every call to an external system needs at least one test that simulates that call failing.
If it does not exist, the code assumes eternal happiness. That is the default failure mode of production services.
</golden_rule>

<stack_awareness>
Tailor recommendations to the detected stack. Quick reference:

- **.NET** → Polly.Simmy (chaos), CsCheck (PBT + parallel), WireMock.Net (HTTP fakes), EF Core logging for N+1
- **TS/Node** → fast-check (PBT), mockttp / nock (HTTP fakes), Toxiproxy client
- **Go** → testing/quick + `-race`, gopter, toxiproxy
- **Rust** → proptest + loom (interleaving exploration)

Detect stack from project files (csproj, package.json, go.mod, Cargo.toml) before suggesting tools.
</stack_awareness>

<output_format>
Report back as:

```
## Production readiness — <feature name>

Applied: <list of playbooks>
Skipped: <list + reason>

### Blockers
- [file:line] <finding> → <fix>

### Warnings
- [file:line] <finding> → <suggestion>

### Notes
- <informational>
```

Keep it terse. No prose paragraphs. If clean, say "clean" for the section.
</output_format>
