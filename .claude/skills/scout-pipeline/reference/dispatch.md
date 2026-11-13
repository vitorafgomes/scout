# Dispatch forms

Copy, fill in, send unchanged.

## To `scout-implementer`

```text
Project: <Scout.X>   Worktree: <path>   Lane: feature | bugfix | refactor
1. Files      EDIT: <paths, context pages included>   READ: <paths>
2. Steps      <in order, with the test class of each>
3. Rules at risk   <line of the rules file -> the test that pins it>
   Calls leaving the process: <none | read | write | irreversible write, declared by <feature>>
   Vendor boundary: <none | behind <abstraction>>
4. Acceptance <literal commands>
5. Out of scope <...>
6. Requirements   <requirement, in the user's words> -> <test path::name> [new behaviour | behaviour lock | mutation proof]
```

## To the `scout-review` workflow, as its `dispatch` argument

```text
Project: <Scout.X>   Diff: working tree of <worktree> (or <range>)
Lenses: invariants[, read-only][, secrets][, boundaries]   (the row of reference/sensitive.md that names each)
Requirements: <rows with their kind>
Proof: <per row: the mutation, the quoted failure, the restore; or the runs before and after>
Test quality: test-anti-patterns <result>; assertion-quality <result>
```

Call it once per change:
`Workflow({ name: "scout-review", args: { module: "<Scout.Project>", lenses: [...], dispatch: "<the form above, filled>" } })`.
When the Workflow tool is not available, send the same form to `scout-reviewer` directly, once
per lens.
