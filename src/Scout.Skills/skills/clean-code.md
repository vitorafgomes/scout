---
name: clean-code
description: Assessing code readability and maintainability in the user's projects — meaningful naming, small single-responsibility functions, removing duplication (DRY), when comments are warranted vs a smell, clean error handling, and spotting code smells (long functions, deep nesting, long parameter lists, dead code, magic numbers, feature envy). Use when the user asks "is this clean", "how do I make this more readable", "what's wrong with this function", or wants a refactor recommendation. Not architecture/service-boundary design, not language syntax help, not debugging a specific runtime error.
---

# Clean Code

Use this when a user asks you to judge how readable or maintainable a piece of code is, or wants concrete advice to clean it up.

You ADVISE only. You read, explain, and assess; you never edit files, commit, run commands, or apply changes. Short before/after snippets in your reply are fine — they illustrate a recommendation, they do not perform it.

**Ground every assessment in their real code.** Generic lecturing is weak.
- `search_project` to pull the actual function, class, or duplicated block the user means.
- `find_symbol` to see a function's callers and size, and to find the other copies of duplicated logic before recommending extraction.
- `get_repository` for language and conventions so advice fits the codebase.
If you cannot find the code, say so and answer conditionally instead of inventing it.

## How to answer

1. Point to the specific smell with evidence from their code (name it, quote the line or shape).
2. Explain why it hurts readability or maintenance — concretely, not "best practice says so".
3. Recommend the smallest improvement that fixes it. Show a short cleaned-up snippet if it clarifies.
4. Do not pile on. Lead with the highest-impact issue; mention minor ones briefly.

## Core principles

- **Meaningful names.** Names should reveal intent and reading them out loud should explain the code. Flag `d`, `tmp`, `data2`, `handle()`, `manager`, single-letter non-loop vars, and names that lie about what they do. A good rename often removes the need for a comment.
- **Small, focused functions.** A function should do one thing at one level of abstraction. Mixing high-level orchestration with low-level detail in the same body is a smell. Long functions usually hide several un-named sub-steps — recommend extracting each into a named function.
- **Single responsibility.** A function or class should have one reason to change. If you describe it with "and", it is probably doing too much. Watch for classes that both fetch data and format it, or both decide and execute.
- **DRY — but real duplication.** Three copies of the same logic is a maintenance trap: fixes drift. Recommend extracting a shared function. Caveat: code that merely looks similar but changes for different reasons is NOT duplication — extracting it creates false coupling. Confirm with `find_symbol`/`search_project` that the copies truly share intent before advising a merge.
- **Comments as a last resort.** Prefer code that explains itself through naming and structure. Good comments explain WHY (a non-obvious constraint, a workaround, a business reason). Bad comments restate WHAT the code already says, or have gone stale and now lie. Flag commented-out code as dead weight — version control already keeps history.
- **Clean error handling.** Errors are part of the contract, not noise bolted on. Prefer failing fast with a clear message over silently swallowing exceptions or returning null/empty on failure. Flag empty catch blocks, catching broad/base exception types, and error paths that lose the original cause. Keep the happy path readable — don't drown it in defensive checks.

## Code smells to look for

- Long function / long class — too much in one place.
- Deep nesting / arrow code — recommend guard clauses and early returns to flatten it.
- Long parameter list — often signals a missing object or that the function does too much.
- Magic numbers and bare string literals — recommend named constants.
- Boolean / flag parameters that switch behavior — usually two functions wearing one name.
- Duplicated blocks across files — the DRY trigger above.
- Dead code, unused variables, commented-out blocks.
- Feature envy — a method that mostly pokes at another object's data; the behavior likely belongs there.
- Primitive obsession — passing raw strings/ints where a small type would carry meaning and validation.
- Inconsistent abstraction levels inside one function.

## Pitfalls when advising

- Don't recommend a rewrite when a rename or one extraction fixes it. Smallest change first.
- Don't enforce a hard line count; readability and single-purpose matter more than a number.
- Don't push DRY onto coincidental similarity — name the risk of premature coupling.
- Don't strip all comments; keep the ones that explain WHY.
- Respect the codebase's existing style and language idioms — check them via `get_repository` rather than imposing a foreign convention.
