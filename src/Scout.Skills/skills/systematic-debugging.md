---
name: systematic-debugging
description: Use when a user reports a bug, test failure, crash, or unexpected behavior and wants help reasoning about the cause before any fix
---

# Systematic Debugging

## Overview

When a user pastes a bug, error, or stack trace, do not jump to a fix. Random guesses waste their time and mask the real issue.

**Core principle:** Reason toward the root cause first. Describe the diagnosis. A fix that only addresses the symptom is a failure.

## The Iron Law

```
NO RECOMMENDED FIX WITHOUT ROOT-CAUSE REASONING FIRST
```

Walk through the investigation phases before suggesting any change.

## When to Use

Any reported technical issue:
- Test failures
- Production bugs
- Unexpected behavior
- Performance problems
- Build failures
- Integration issues

**Apply especially when:**
- The user is in a hurry (rushing guarantees rework)
- A "quick fix" seems obvious
- Previous fixes did not work
- Nobody fully understands the issue yet

## The Four Phases

Reason through each phase in order before recommending a change.

### Phase 1: Root Cause Investigation

1. **Read the error carefully**
   - Use the exact message, stack trace, line numbers, file paths, error codes
   - They often name the problem directly

2. **Establish reproduction**
   - Ask what the exact steps are and whether it happens every time
   - If not reproducible, gather more data before theorizing

3. **Identify recent changes**
   - What changed near this code or config?
   - New dependencies, config edits, environment differences
   - Use `search_project` and `find_symbol` to locate the affected code; `get_repository` for recent project context

4. **Locate the failing boundary in multi-component systems**
   - When data crosses components (API -> service -> database), reason about each boundary
   - For each boundary, ask: what enters, what exits, is config/state correct there?
   - Recommend the user check or log each boundary to find WHERE it breaks, then narrow to that component

5. **Trace the data flow**
   - Where does the bad value originate?
   - What passed it in? Keep tracing upward to the source
   - Recommend fixing at the source, not at the symptom
   - Use `find_symbol` to follow callers and definitions across the codebase

### Phase 2: Pattern Analysis

1. **Find working examples** — locate similar code that works (`search_project`); contrast it with the broken path
2. **Compare against references** — read the reference implementation fully before assuming how the pattern should behave
3. **List every difference** between working and broken, however small — do not dismiss any as "can't matter"
4. **Map dependencies** — what config, environment, or other components does the broken code assume?

### Phase 3: Hypothesis and Testing

1. **State one hypothesis** — "X is the root cause because Y." Specific, not vague.
2. **Recommend the smallest possible test** — change or check one variable at a time to confirm or refute it
3. **Resolve before continuing** — if confirmed, move on; if refuted, form a NEW hypothesis instead of stacking guesses
4. **Admit unknowns** — if part of the system is unclear, say so and recommend gathering more evidence; do not pretend

### Phase 4: Recommending the Fix

1. **Describe a reproduction first** — recommend the simplest case that triggers the bug, so the fix can be proven
2. **Recommend a single change** — address the identified root cause, one change, no bundled refactoring
3. **State how to verify** — the bug is resolved and no other behavior regresses
4. **If the fix would not work / fixes keep failing**
   - Count the attempts
   - Fewer than 3: return to Phase 1 with the new information
   - **3 or more failed fixes: stop and question the architecture (below)**

5. **After 3+ failed fixes: question the architecture**

   Signs the design itself is wrong:
   - Each fix exposes new coupling or shared state elsewhere
   - Each fix needs sprawling refactoring
   - Each fix creates new symptoms in another place

   Recommend the user step back and ask:
   - Is this pattern fundamentally sound?
   - Are they keeping it only out of inertia?
   - Is a redesign cheaper than continuing to patch symptoms?

   This is not a failed hypothesis — it is a wrong architecture. Surface that explicitly.

## Red Flags — Stop and Reason

If you are about to:
- Offer a quick fix "for now"
- Say "just try changing X"
- Bundle multiple changes at once
- Propose a solution before tracing the data flow
- Suggest a 3rd+ fix attempt on the same issue

Stop and return to Phase 1.

## Common Rationalizations

| Excuse | Reality |
|--------|---------|
| "Issue is simple, skip the process" | Simple bugs have root causes too; the process is fast for them |
| "Emergency, no time" | Systematic reasoning beats guess-and-check thrashing |
| "Just try this first" | The first answer sets the pattern; do it right |
| "Reference is long, I'll adapt it" | Partial understanding guarantees a wrong recommendation |
| "I see the problem" | Seeing the symptom is not understanding the cause |
| "One more fix" (after 2+ failures) | 3+ failures means an architectural problem; question the design |

## When Reasoning Reveals "No Root Cause"

If investigation shows the issue is genuinely environmental, timing-dependent, or external:

1. Note what was ruled out
2. Recommend appropriate handling (retry, timeout, clearer error message)
3. Recommend monitoring/logging for future investigation

But: most "no root cause" cases are incomplete investigation. Push the reasoning further first.
