# Bug-fix lane

1. **Read.** The project's rules file and the pages it names for the code the bug is in. If the
   bug breaks a written rule, the rule is the target of the fix, not the symptom.
2. **Reproduce first.** The failing test is step one of the plan, written and seen red before the
   fix. A bug in the write gate, in credential handling or in provider selection has a test that
   fails on the old behaviour.
3. **Find the cause, then make the smallest fix.** Search every caller of the function you are
   about to touch. One guard in the shared path covers every caller; a guard on the path the
   report names leaves every other adapter and provider broken. Nothing around the fix is tidied.
4. **Check, prove, review, record** (`SKILL.md` steps 5 to 8). A fix inside
   `Scout.ControlPlane.Contracts` or a shared project has the wide test surface of
   `reference/test-surface.md`.
5. **Write the rule down.** A bug that came from a rule nobody had written becomes an invariant
   page in the same change.
