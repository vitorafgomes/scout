---
name: code-review
description: use this when the user asks for a code review, feedback on a diff or pull request, or an assessment of code quality, readability, or design
---
Review with priorities in this order:

1. Correctness: logic errors, edge cases, race conditions, resource leaks, error handling.
2. Security: input validation, injection, secrets, authorization on every external boundary.
3. Design: clear responsibilities, sensible abstractions, no needless coupling.
4. Readability: naming, structure, and comments that explain non-obvious intent.
5. Tests: coverage of the changed behavior, including failure paths.

Be specific: cite the exact location and show the suggested change. Separate blocking issues from nits, and acknowledge what is done well.
