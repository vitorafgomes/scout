---
title: "Skills: Public contract"
version: "1.0"
date: 2026-11-13
changes: "Split out of the Skills service page."
page_type: service
status: active
description: "Read when a change touches: Public contract."
source: ["src/Scout.Skills/", "tests/Scout.Skills.Tests/"]
reliability: high
---

## Public contract

- **Entry point:** `AddScoutSkills(IServiceCollection, IConfiguration)` at `src/Scout.Skills/SkillsServiceCollectionExtensions.cs:29`.
- **Abstractions:** `ISkillSelector` (`src/Scout.Skills/ISkillSelector.cs:8`), `ISkillCatalog` (`src/Scout.Skills/ISkillCatalog.cs:9`), record `Skill` (`src/Scout.Skills/Skill.cs:16`) with the `Functions` it declares (`src/Scout.Skills/Skill.cs:22`), `NoOpSkillSelector` (`src/Scout.Skills/NoOpSkillSelector.cs:7`), `SkillPromptComposer` (`src/Scout.Skills/SkillPromptComposer.cs:10`).
- **Model tools:** plugin `Skills` with `list_skills` (`src/Scout.Skills/SkillsPlugin.cs:22`) and `load_skill` (`:35`), registered as a `KernelPlugin` (`src/Scout.Skills/SkillsServiceCollectionExtensions.cs:63`).
- **Configuration keys:** `Scout:Skills` with `Enabled` (default true), `Directory` (default `skills`, relative to the binary directory), `TopN`, `SimilarityThreshold` (`src/Scout.Skills/SkillsOptions.cs:8`).
- **External boundaries:** filesystem read of `*.md` under the skills directory (`src/Scout.Skills/SkillFileLoader.cs:40`, `:67`); embedding calls through `IEmbeddingGenerator` (`src/Scout.Skills/SkillSelector.cs:35`).
