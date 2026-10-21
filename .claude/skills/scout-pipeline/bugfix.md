# Bug-fix lane

1. **Vault route** - `vault/pages/services/<project>.md` and the invariants touching the bug surface.
   If the bug violates a documented invariant, that invariant is the fix target, not the symptom.
2. **Reproduce first** - once the expected behaviour is a defined spec, the failing test is step one
   of the plan and `scout-implementer` writes it, red, before the fix. Exit: a test that
   demonstrates the bug. Proof that it goes red when the fix is removed is gate 5 in SKILL.md. A
   bug in the read-only guard, in credential handling or in provider selection needs a test that
   fails on the *old* behaviour; nothing else proves it.
3. **Root cause, then smallest fix** - grep every caller of the function you are about to touch: one
   guard in the shared path beats a guard per caller, and patching only the path the report names
   leaves every sibling caller (every adapter, every provider) broken. No surrounding cleanup
   (Rule C).
4. **Scans (diff-gated)** - only if the fix touched a streaming path, an index or a provider call
   shape. A typical small fix runs zero scans.
5. **Gates 3-9** from SKILL.md, including gate 4's surface table and **Prove the requirement** - a
   fix inside `Scout.ControlPlane.Contracts` or a shared project has a far wider surface than one
   test project.
6. **Crystallize** - a bug that came from a rule nobody had written down becomes an invariant page,
   not just a code change. The implementer writes the page the plan names; the manager reviews it
   (SKILL.md gate 7).
