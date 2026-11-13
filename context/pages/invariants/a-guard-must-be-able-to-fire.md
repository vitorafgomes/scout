---
title: "A guard must be able to fire"
version: "1.1"
date: 2026-11-11
updated: 2026-11-11
changes: "v1.1 (2026-11-11): the in-memory credential store is ruled on and leaves the known exceptions."
page_type: invariant
status: active
description: "Every check that rejects an input has an input that reaches it and a test that supplies one; a check the code before it makes unreachable is a defect, and a failed operation never answers as if it worked."
enforced_by:
  - ".claude/agents/scout-reviewer.md"
  - "src/Scout.Skills/skills/code-review.md"
  - "src/Scout.Skills/skills/code-audit.md"
verified_by:
  - "tests/Scout.Skills.Tests/BundledSkillsTests.cs"
source:
  - chat
  - ".claude/agents/scout-reviewer.md"
reliability: medium
---

# A guard must be able to fire

> Every check that rejects an input has an input that reaches it and a test that supplies one. A check the surrounding code makes unreachable is worse than no check: it reads as protection, it passes review, and the case it was written for takes the unhandled path.

## Why this exists

The audit Scout ran over its own repository on 2026-11-10 reported, among the findings a failing test confirmed on 2026-11-11, one of this shape in code that had been read and reviewed:

- **The element the validation dereferences.** A list of comments that holds a null element made the validation of a pull request verdict throw a null reference on the first property it read, where every other invalid comment is refused with the validation error the caller understands (issue #296). The modelled outcome existed and could not happen for that input.

The same audit confirmed the sibling failure, an operation that fails and answers as if it worked: a model download whose stream carried an error line and ended normally (issue #290), and a start that marked the local model server ready whatever came back (issue #292). A caller cannot handle a failure it is told did not happen.

The rule cuts both ways. The audit also reported a check that looked unreachable and was not (issue #248: a repeated scene id is refused by the validation that runs before the merge the finding pointed at). Whether a guard can fire is settled by a test that supplies the input, not by reading the guard alone.

## Scope

- **Applies to:** every validation, precondition and error branch in `src/**` and `ui/**`; every review of a diff by `scout-reviewer`; every review and audit Scout runs for a user through the `code-review` and `code-audit` skills.
- **Does not cover:** a defensive check for a case the types already prevent. That is not a guard that cannot fire, it is work that buys nothing, and the fix is to remove it, not to test it ([[pages/conventions/decisions-that-look-like-defects]]).

## The rule

1. A new or changed guard ships with a test whose input makes it fire and that asserts the outcome the guard promises (the exception type, the refusal, the result), not that a call happened ([[pages/conventions/testing]]).
2. Nothing between the entry point and the guard may throw, return or dereference for the input the guard exists to refuse. Read the lines above the guard with that input in mind.
3. A condition the surrounding code makes impossible is deleted or the code is changed so it can occur; it is never left as documentation.
4. A failed operation answers as a failure: no success value, no ready mark, no empty result that reads as "nothing there".

## Where it's enforced

- `.claude/agents/scout-reviewer.md`: the `invariants` lens names both shapes in its correctness check and reports a guard with no test that fires it.
- `src/Scout.Skills/skills/code-review.md` and `src/Scout.Skills/skills/code-audit.md`: the review of a pull request and the audit of a repository report both shapes as findings.

There is no analyzer for it; the enforcement is the review and the test Rule B already asks for.

## How it's verified

- `tests/Scout.Skills.Tests/BundledSkillsTests.cs` pins that both bundled skills carry the rule.

## Known exceptions

Code that breaks the rule today, listed until it is fixed or ruled on: none.

Ruled on, kept here so the next audit finds the answer:

- **The in-memory credential store, ruled on 2026-11-11.** It answers that it stored a secret while it reports itself unavailable (`src/Scout.ControlPlane/Features/InMemoryCredentialStore.cs`). It does keep the secret while Scout runs, so that answer is not a failure reported as success, and the store's contract now says so: unavailable means a stored secret lasts only while Scout runs, and false means nothing was stored (`src/Scout.ControlPlane/Features/ICredentialStore.cs`). A false from the store still refuses the save on both callers (`tests/Scout.ControlPlane.Tests/Features/FeaturesServiceTests.cs`, `SetCredentials_Fails_As_Not_Persisted_When_The_Credential_Store_Rejects_The_Secret`). [[pages/conventions/decisions-that-look-like-defects]] records the ruling and why no owner is warned yet.

The three exceptions listed here on 2026-11-11 (issues #290, #292 and #296) were fixed the same day and left the list.

## Related

- Conventions that reference this: [[pages/conventions/testing]], [[pages/conventions/tooling]], [[pages/conventions/decisions-that-look-like-defects]]
- Services bound by this: [[pages/services/skills]]
- Similar invariants: [[pages/invariants/no-executive-writes]] (a write that is refused must be refused before it is sent)

## Change protocol

If a future change must violate this invariant:

1. Draft a superseding ADR under `pages/decisions/` explaining why.
2. Update this page: set `status: deprecated`, set `superseded_by:`.
3. Replace the enforcement text with the new rule the ADR establishes.
