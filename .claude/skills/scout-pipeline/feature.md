# Feature lane

Stages in order. Repo facts live in the linked vault pages; this file carries only the stage order.

0. **Discovery** - when the request is an owner sentence with no spec in the vault yet, run
   `superpowers:brainstorming` and `product-management:write-spec` first, write the outcome into a
   roadmap page under `vault/pages/roadmap/` (problem, users, scope, non-goals, acceptance, open
   decisions), and only then continue.
1. **Pre-flight** - decide before coding: which project owns it (a feature that seems to need two
   projects usually needs one project plus a contract in `Scout.ControlPlane.Contracts` or a
   Semantic Kernel function); the write classification of every call that leaves the process
   (descriptive to which channel, or none; executive is not an option); the vendor boundary it
   crosses and the abstraction it goes through; where the configuration lives and how a contributor
   without paid credentials runs it.
2. **Vault route** - `vault/pages/services/<project>.md` + linked invariants + relevant ADRs, **then
   the capability check in SKILL.md gate 1**. A feature is where a project first gains an outbound
   call, an LLM-invocable function or a new error path, and the project page carries no rules for a
   capability it does not yet have. Exit: no invariant conflict (else stop, superseding ADR first).
3. **Plan** - state the six-part plan from SKILL.md before implementation; a sequence diagram when
   the flow crosses the control plane or a chat platform.
4. **Implement** - dispatch `scout-implementer` (opus) with the six-part plan, then check its diff
   against the plan and report any departure to the owner. Rule B per edit. New adapter, provider
   or plugin registers through the existing abstraction and ships its failing-call test (Rule I).
5. **Scans (diff-gated)** - per `AGENTS.md`; the test-quality skills run as part of gate 5. A scan
   whose surface the diff does not touch does not run.
6. **Gates 3-9** from SKILL.md (build, format and diagnostics -> full test surface -> **Prove the
   requirement** -> review workflow -> crystallize -> PR body -> commit message). A brand-new
   project or slice uses `mutation proof` (SKILL.md gate 5).
