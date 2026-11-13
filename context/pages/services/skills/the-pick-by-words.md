---
title: "Skills: The pick by words (2026-11-08)"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Skills service page."
page_type: service
status: active
description: "Read when a change touches: The pick by words (2026-11-08)."
source: ["src/Scout.Skills/", "tests/Scout.Skills.Tests/"]
reliability: high
---

## The pick by words (2026-11-08)

Whenever embeddings cannot answer, with none at all, a failed index build or a failed query embedding, `SelectAsync` picks by the words of the message (`src/Scout.Skills/SkillSelector.cs:90`, `:140`) through `LexicalSkillIndex`, built once from the loaded skills on the first build (`:178`), with no model call and nothing leaving the process ([[pages/decisions/0054-functions-scoped-per-skill-and-job]]). Words are lower-cased, accents folded, and short, numeric and common English and Portuguese words dropped, the generic "repository", "project" and "code" among them (`src/Scout.Skills/LexicalSkillIndex.cs:24`); two words match when equal or when one of at least four letters starts the other. A skill is indexed by its name, counted three times, and its description up to its first "Not for" clause (`:22`), so a skill's own exclusions never pick it. The score is the share of the message's known words, each weighted by how few skills it matches, that the skill matches; the best skill at or above 0.55 (`:18`), most matches first on a tie, is picked, and only when it declares functions (`:85`): a skill that would change nothing the turn is offered stays in the catalog for the model to load, as before. The sample questions in both languages and what they pick are `tests/Scout.Skills.Tests/LexicalSkillIndexTests.cs:39` (picks) and `:56` (none); the selector paths are `tests/Scout.Skills.Tests/SkillSelectorTests.cs:122`, `:141`, `:215`. After a failed query embedding the messages of the next `IndexRetryInterval` (one minute) skip the embedding and go straight to the words (`src/Scout.Skills/SkillSelector.cs:105`, set at `:136`), so a hanging embedder costs one timeout per minute, not one per call, including the router's second, own-text selection (`tests/Scout.Skills.Tests/SkillSelectorTests.cs:232`).
