# Refactor lane

1. **ADR check** - read `vault/pages/decisions/*` that established the current shape. A refactor that
   contradicts an accepted ADR needs a superseding ADR FIRST, not code. If no ADR governs the shape,
   say so and continue.
2. **Capability check** - run the table in SKILL.md gate 1 before the first edit. A refactor that
   moves a call out of an abstraction, changes which project talks to a vendor SDK, or changes where
   a configuration value is read is adding a capability, not just moving text.
3. **Behaviour lock** - mark every refactor entry in the requirement-to-test map as `behaviour lock`
   and confirm the named tests pin the behaviour being restructured. Each named test must be green
   before the restructure and green after it; a test that is red before shows the change is a
   feature or fix, so stop and reclassify it. Pinning tests for uncovered branches are step one of
   the plan, written by `scout-implementer` BEFORE it moves code. Exit: a green baseline to diff
   against. If the refactor changes nothing executable, say so explicitly rather than silently
   skipping this stage.
4. **Restructure** - dispatch `scout-implementer` (opus) with the plan and check its diff against
   it. Rule B per edit; no behaviour change; respect the adapter, provider and plugin abstractions
   (Rule F); no new abstraction with one user (Rule C).
5. **Scans (diff-gated)** - the test-quality skills run as part of gate 5; `scout-optimizer` when
   the restructure touches a hot path named in SKILL.md gate 6.
6. **Gates 3-9** from SKILL.md, including **Prove the requirement** (gate 5): every `behaviour lock`
   test green before and after, the full surface green, and a refactor that moves code *into*
   `Scout.ControlPlane.Contracts` widens that surface to every project.
7. **Crystallize** - use `wiki-crystallize` to specify updates to any vault reference the refactor
   moved (grep the vault for the old paths); the implementer writes them as the plan names and the
   manager reviews them. A ref that still resolves proves the file exists, never that it still
   describes the claim.
