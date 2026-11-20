---
title: "Skills: Testing footprint"
version: "1.1"
date: 2026-11-13
changes: "v1.1 (2026-11-20): the tests of the review notes and review inputs. Split out of the Skills service page."
page_type: service
status: active
description: "Read when a change touches: Testing footprint."
source: ["src/Scout.Skills/", "tests/Scout.Skills.Tests/"]
reliability: high
---

## Testing footprint

`tests/Scout.Skills.Tests` (109 tests on 2026-11-08, 11 files; the pick by words in `LexicalSkillIndexTests.cs`, the functions line in `SkillFileLoaderTests.cs`): loader parsing and skips, catalog lookup, selector ranking with a fake embedder (`FakeEmbeddingGenerator.cs`), the index built once under concurrent first calls, retried after a failure only once the interval has passed (a `FakeTimeProvider`), the catalog offered and the one log line when there are no embeddings and none when there are (`SkillSelectorTests.cs`), prompt composition including the `# Skills` section and the summary rule, the section over the real bundled skills (`BundledSkillsTests.cs`, `Without_Embeddings_The_System_Prompt_Routes_A_Review_To_Code_Review_And_A_Whole_Repository_To_Code_Audit`, `The_Skills_Section_Lists_Every_Bundled_Skill_Once_With_A_Short_Summary_Under_12000_Characters`), plugin metadata and behaviour, and DI registration including the disabled path.

- **2026-11-20 (ADR 0064):** `BundledSkillsTests::What_A_Pull_Request_Removes_And_What_It_Says_It_Does_Are_Judged`, `::A_Departure_From_A_Written_Rule_Is_A_Finding_That_Names_It_In_Rule`, `::A_Test_Deleted_While_Its_Code_Stays_Is_A_Finding_At_Line_1`, `::A_Finding_May_Be_Anchored_At_Line_1_Of_A_File_The_Pull_Request_Deletes`. `Scout.Skills.Tests`: 258 passed, 0 skipped.
