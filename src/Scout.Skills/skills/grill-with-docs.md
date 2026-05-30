---
name: grill-with-docs
description: Stress-test plans against domain model — validate terminology, expose gaps, update CONTEXT.md and ADRs inline
---

# Grill with Docs

**Purpose**: Refine a plan by stress-testing it against the project's existing domain model, terminology, and documentation. Captures decisions in CONTEXT.md (glossary) or ADRs as they crystallize.

**When to use**:
- After `superpowers:writing-plans` or `gsd-plan-phase` to validate architectural decisions
- When planning features that touch core domain concepts (WayUp Notifications, Campaigns, etc.)
- Before implementation to lock terminology and avoid mid-build rework
- When your plan conflicts with existing documented patterns or glossary

**Prerequisite**: Project must have `CONTEXT.md` in the Obsidian vault at `~/WorkSpace/obsidian-vault/<project>/CONTEXT.md`

## Workflow

### Phase 1: Structured Interview
1. Read the provided plan carefully
2. Ask one focused question per turn about a specific aspect (scope, constraints, terminology, boundaries)
3. Wait for user feedback before moving to next question
4. Do NOT try to answer for the user

**Question progression**:
- Scope: What is in/out of scope?
- Domain boundaries: How does this concept relate to existing domain terms?
- Terminology: Does this align with CONTEXT.md glossary? Any new terms?
- Constraints: What are hard constraints from existing code/architecture?
- Trade-offs: What are we sacrificing? Any alternatives?

### Phase 2: Codebase Validation
When user answers, search the codebase for evidence:
- Existing implementations of related concepts
- Where terminology is used (grep for key terms)
- Domain model examples (search for entity definitions, enum values, etc.)
- Patterns that support or contradict the plan

Report findings as "what the code shows" — use this to refine questions.

### Phase 3: Terminology Alignment
1. Extract all new or ambiguous terms from the plan
2. Cross-check against CONTEXT.md glossary
3. Flag conflicts: "Plan says X, but CONTEXT.md defines X as Y"
4. Propose updates to glossary or plan to resolve

### Phase 4: Capture Decisions
As decisions solidify:
- **Glossary updates** → add/refine entries in CONTEXT.md (pure domain terms, no rationale)
- **ADR-worthy decisions** → only if the decision is hard to reverse, unexpected without context, and trades off genuine alternatives
  - Example: "Why we store Notification Index as nullable integer instead of separate status field"
  - Not: "We use SQL Server" (infrastructure, not domain)
- **Inline in plan** → minor clarifications that don't warrant docs

End result: plan + updated CONTEXT.md + any new ADRs, all ready for implementation.

## Key Principles

1. **One question at a time** — no rapid-fire interrogation. Let user think and respond fully.
2. **Validate, don't assume** — check the codebase before accepting an answer. Say "I found X in the code; does this match your intent?"
3. **Terminology is sacred** — conflicts between plan and CONTEXT.md must be resolved before moving forward.
4. **ADRs are expensive** — only capture decisions that will haunt you later if forgotten.
5. **Plan owns the output** — refine the plan based on findings, don't rewrite from scratch.

## Example: WayUp Notification Index

**User plan**: "Notification Index should auto-reorder when a notification is disabled."

**Q1**: "Does 'disabled' mean soft-delete or a separate status flag?"
→ User answers → Check code (find NotificationStatus enum, see how Campaigns handles disabling)
→ Refine based on what code shows

**Q2**: "Does reordering apply only to active notifications, or all?"
→ User clarifies scope → Validate against existing Index usage in codebase

**Q3**: "Is Index guaranteed unique per user, or per list?"
→ Confirm via schema/entity definitions → Update plan if ambiguous

**Outcome**: Plan is precise, terminology is locked to CONTEXT.md, ADR captures the why (why nullable Index means clear, why reorder on disable).

---

**Related skills**: `superpowers:writing-plans`, `gsd-plan-phase`, `gsd-research-phase`
