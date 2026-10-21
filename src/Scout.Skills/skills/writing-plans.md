---
name: writing-plans
description: Use when the user asks you to lay out an implementation plan, break a feature or change into ordered tasks, or describe step by step how a multi-part change should be built in their project
---

# Writing Plans

## Overview

When asked to plan a multi-step change, describe a comprehensive plan for an implementer (a human or another agent) who has zero context for the codebase. Cover everything they need: which files to touch per task, what code goes where, how to test it, and what to check. Deliver the whole thing as bite-sized tasks. Apply DRY, YAGNI, and test-first thinking.

Assume the implementer is skilled but knows almost nothing about this toolset or problem domain, and is weak at test design. You only describe the plan in chat; you never edit, run, or commit anything.

Ground the plan in the real project. Use `search_project` to find the relevant code, `find_symbol` to confirm exact names and signatures, and `get_repository` for repo structure. Never invent file paths or symbols — verify them first.

## Scope Check

If the request spans multiple independent subsystems, recommend splitting it into separate plans — one per subsystem. Each plan should describe working, testable software on its own.

## File Structure

Before listing tasks, map which files will be created or modified and what each is responsible for. This locks in decomposition.

- Each file should have one clear responsibility, with clean boundaries and a well-defined interface.
- Prefer smaller, focused files over large ones that do too much.
- Files that change together should live together. Split by responsibility, not by technical layer.
- In an existing codebase, follow established patterns. Don't recommend restructuring unless a file being modified has grown unwieldy — then a split is reasonable.

This structure drives the task breakdown. Each task should produce self-contained changes that make sense on their own.

## Bite-Sized Task Granularity

Describe each step as one small action (2-5 minutes of work):
- Write the failing test
- Run it to confirm it fails
- Write the minimal code to make it pass
- Run the tests to confirm they pass
- Record the change as a focused commit

## Plan Header

Start the plan with:

```markdown
# [Feature Name] Implementation Plan

**Goal:** [One sentence on what this builds]

**Architecture:** [2-3 sentences on the approach]

**Tech Stack:** [Key technologies/libraries]
```

## Task Structure

For each task, describe the files involved and the ordered steps the implementer should follow:

````markdown
### Task N: [Component Name]

**Files:**
- Create: `exact/path/to/file.py`
- Modify: `exact/path/to/existing.py:123-145`
- Test: `tests/exact/path/to/test.py`

Steps the implementer should follow:

1. Write the failing test, e.g.

```python
def test_specific_behavior():
    result = function(input)
    assert result == expected
```

2. Run the test and confirm it fails (expected: FAIL, "function not defined").

3. Write the minimal implementation, e.g.

```python
def function(input):
    return expected
```

4. Run the test and confirm it passes.

5. Record the change as a focused commit.
````

## No Placeholders

Every step must carry the actual content the implementer needs. These are plan failures — never produce them:
- "TBD", "TODO", "implement later", "fill in details"
- "Add appropriate error handling" / "add validation" / "handle edge cases"
- "Write tests for the above" without the actual test code
- "Similar to Task N" — repeat the code, since tasks may be read out of order
- Steps that say what to do without showing how (code steps need code)
- References to types, functions, or methods not defined in any task

## Remember
- Exact file paths always (verified with `search_project` / `get_repository`)
- Complete code in every step — if a step changes code, show the code
- Exact commands with expected output
- Names and signatures must match the real code — confirm with `find_symbol`
- DRY, YAGNI, test-first, focused commits

## Self-Review

After describing the full plan, re-read the requirements with fresh eyes and check the plan against them:

1. **Coverage:** For each requirement, can you point to a task that implements it? Note any gaps and add the missing task.
2. **Placeholder scan:** Look for the red flags above and rewrite them with concrete content.
3. **Type consistency:** Do types, method signatures, and property names used in later tasks match what earlier tasks defined? A function called `clearLayers()` in Task 3 but `clearFullLayers()` in Task 7 is a bug — reconcile it.

Fix issues inline as you find them.
