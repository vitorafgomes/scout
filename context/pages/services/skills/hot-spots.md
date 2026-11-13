---
title: "Skills: Hot spots"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Skills service page."
page_type: service
status: active
description: "Read when a change touches: Hot spots."
source: ["src/Scout.Skills/", "tests/Scout.Skills.Tests/"]
reliability: high
---

## Hot spots

- `src/Scout.Skills/SkillSelector.cs:159`: index build on first use under a lock, with the failed-build retry gate; cosine scoring at `src/Scout.Skills/SkillSelector.cs:117`.
- `src/Scout.Skills/SkillPromptComposer.cs:55`: the `# Skills` routing sentence the model reads when there are no embeddings; every bundled skill adds one line to every prompt on such a provider.
- `src/Scout.Skills/SkillFileLoader.cs:40`: recursive scan of the whole directory; a file added there with a `functions` line naming a write offers that write on its turns, which the bundled-skills hash of [[pages/roadmap/security-hardening]] (Smaller items) would close.
- `LexicalSkillIndex.ReviewOrAudit`: the forms of naming a pull request, the review verbs and the whole-repository words are fixed lists in English and Portuguese; a phrasing outside them is scored as before.
- `src/Scout.Skills/LexicalSkillIndex.cs:18`: the 0.55 threshold and the ignored words were tuned on the bundled skills; a new skill's description changes every score.
