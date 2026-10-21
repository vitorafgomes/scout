# Diagnose / investigate lane

**No edits in this lane.** The deliverable is a cited answer; a fix is a separate task through the
bug-fix lane with the requirement-to-test map this lane hands over.

1. **Invoke `superpowers:systematic-debugging`** and follow it: reproduce before theorising, one
   hypothesis at a time, evidence before explanation, and no fix proposed before the cause is shown.
   This lane adds the repo's reading order to that method, nothing else.
2. **Vault first** - the project's "To change here, read" set (`vault/pages/services/<project>.md`),
   its flows and `vault/pages/history/<project>.md`; the incidents under `vault/pages/incidents/`
   for a similar failure.
3. **Evidence second** - the worker's logs and OpenTelemetry output before source (the runbooks
   under `vault/pages/runbooks/` say where); a survey past three queries goes to one `Explore`
   subagent. Symbol questions use the Roslyn LSP, not grep. A hang, a leak or a slow path is traced
   with `dotnet-diag` (`dotnet-trace-collect`, `dump-collect`, `analyzing-dotnet-performance`), not
   reasoned about.
4. **Answer with citations** - `[[vault/pages/...]]` where a page exists, else `file:line`; verified
   facts apart from hypotheses; per Rule D, anything recommended is read in current code first.
   When the diagnosis hands off a fix, carry the user's wording and a requirement-to-test map
   (`new behaviour` | `behaviour lock` | `mutation proof`) into the bug-fix lane.
5. **Crystallize** - an incident page (`vault/templates/_incident.md`) when a running instance was
   affected, otherwise the finding on the project's history page; one `vault/log.md` line.
