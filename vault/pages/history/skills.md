---
title: "Skills - history"
version: "1.0"
date: 2026-10-22
changes: "Initial history note: a failed skill index build is retried instead of cached"
page_type: reference
status: artefact
description: "What changed in the Skills project and when."
source:
  - chat
  - src/Scout.Skills/SkillSelector.cs
  - tests/Scout.Skills.Tests/SkillSelectorTests.cs
reliability: high
---

# Skills - history

> Dated changes to [[pages/services/skills]]; the service page states how it works now.

## 2026-10-22 - a failed skill index build is no longer cached until restart

- **What changed:** `SkillSelector` built its index through a `Lazy<Task<...>>` whose build caught every exception and returned an empty list, so one failed embeddings call (the owner hit a provider 429) turned skills off until the worker restarted, even after the provider recovered. The `Lazy` is replaced by a cached `Task` behind a lock: a successful build, including an empty one when no skill files exist, is cached as before; a build that throws is not, and the next attempt waits `IndexRetryInterval` (one minute, a fixed constant) on a `TimeProvider` the constructor takes as an optional parameter, defaulting to the system clock, so the DI registration is unchanged.
- **Behaviour:** the message whose build fails still gets no skills and no exception. Later messages within the interval get no skills without an embeddings call; the first message after it builds again. Concurrent first callers still share one build.
- **Tests:** `A_Failed_Index_Build_Is_Retried_After_The_Interval`, `A_Failed_Index_Build_Is_Not_Retried_Before_The_Interval` and `Concurrent_First_Calls_Build_The_Index_Once` are new in `tests/Scout.Skills.Tests/SkillSelectorTests.cs`; `SelectAsync_Builds_Index_Once_Across_Calls` holds the success cache. The test project now references `Microsoft.Extensions.TimeProvider.Testing`.
- **Not changed:** the retry interval is not configurable; it moves to `SkillsOptions` if an operator needs to tune it.
- **Invariants:** [[pages/invariants/no-executive-writes]] holds; the retry repeats the same read-only embeddings call.
