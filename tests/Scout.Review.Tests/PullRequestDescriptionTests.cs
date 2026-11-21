namespace Scout.Review.Tests;

/// <summary>
/// What the description of a pull request gives, read by code: its sections and which are filled, what was asked, and
/// which requests no test names. The fixtures follow this repository's pull request template.
/// </summary>
public sealed class PullRequestDescriptionTests
{
    /// <summary>This repository's pull request template, as `.github/PULL_REQUEST_TEMPLATE.md` holds it.</summary>
    private const string Template = """
        ## :clipboard: Checklist

        <!-- Tick what is true. A box left empty is explained under "Not done, and why". -->
        - [ ] My commits follow the project's guidelines
        - [ ] Tests for the change were added
        - [ ] Documentation was updated, or none is needed
        - [ ] This description names every request
        - [ ] Code and comments are in English
        - [ ] No secrets or credentials committed
        - [ ] If this touches the control-plane contracts (`Scout.ControlPlane.Contracts`), the feature
              catalog, the LLM provider abstraction, or the chat adapter abstraction, an ADR has been drafted
        - [ ] `dotnet format` clean and tests green locally

        ## :gear: What kind of change is this?

        <!-- Tick the main kind. -->
        - [ ] Bugfix
        - [ ] Feature
        - [ ] Code style
        - [ ] Refactoring, no functional change
        - [ ] Build
        - [ ] Documentation
        - [ ] Tests
        - [ ] Other (describe)

        ## :dart: What was asked

        <!-- One line per request: its number, what was asked, the issue it comes from. -->
        - **R1**

        ## :hammer_and_wrench: What was done

        <!-- One line per request, then its main files. -->
        - **R1**
          - Main files:

        ## :construction: Not done, and why

        <!-- A request left out and the reason, or "Nothing". -->
        -

        ## :heavy_plus_sign: Done beyond what was asked, and why

        <!-- Anything in the diff that no request asked for. -->
        -

        ## :test_tube: Tests implemented

        <!-- One row per test. Say whether it was seen failing before the change. -->
        | Request | Test | File | What it proves | Seen failing before the change |
        |---|---|---|---|---|
        | | | | | |

        ## :raised_hand: Tests to run by hand

        <!-- What the tests do not cover, as steps a person can follow. The reviewer ticks them. -->
        <!-- Each case: its number, a title, and the request it proves. -->
        - [ ] **1.**
          - Set up:
          - Steps:
          - Expected:

        ## :books: Documentation

        <!-- "None" is an answer. -->
        - Updated:
        - Decision record followed:
        - Deliberately not documented:

        ## :rocket: How to run it

        <!-- The commands, one per line. -->
        ```text

        ```

        ## :warning: Risk and impact

        <!-- "Not touched" and "None" are answers. -->
        | Item | Answer |
        |---|---|
        | Public contract | |
        | Data | |
        | Settings | |
        | Dependencies | |
        | Security | |
        | How to roll back | |

        ### Write contract

        <!-- Every write Scout makes is declared by its feature, granted by the owner
             in the desktop, approved per action when irreversible, and logged
             (context/pages/decisions/0032-per-feature-write-permissions.md). -->

        - [ ] Every call this PR adds or changes that leaves the process is declared by its feature (read, write or irreversible write); a write runs only under the owner's grant, an irreversible one only after the owner's per-action approval, and every write is logged.
        - [ ] Nothing in this PR writes or edits code, or writes on the host outside Scout's own folders.

        ## :eyes: For the reviewer

        <!-- Where to start and where to look hardest. Fill N and M from the review of this change. -->
        - Start reading at:
        - Look hardest at:
        - Out of scope:
        - Known weak points:
        - Review: applied N, rejected M

        ## :thumbsup: Reviewers

        <!-- A mention of the review bot on the last line asks for its review. -->
        """;

    private static readonly string[] TemplateTitles =
    [
        "Checklist", "What kind of change is this?", "What was asked", "What was done", "Not done, and why",
        "Done beyond what was asked, and why", "Tests implemented", "Tests to run by hand", "Documentation", "How to run it",
        "Risk and impact", "For the reviewer", "Reviewers",
    ];

    /// <summary>A description that fills the template as an author of this repository would, for a change of the desktop app.</summary>
    private const string Filled = """
        ## :clipboard: Checklist

        <!-- Tick what is true. A box left empty is explained under "Not done, and why". -->
        - [x] My commits follow the project's guidelines
        - [x] Tests for the change were added
        - [x] Documentation was updated, or none is needed
        - [x] This description names every request
        - [x] Code and comments are in English
        - [x] No secrets or credentials committed
        - [ ] If this touches the control-plane contracts (`Scout.ControlPlane.Contracts`), the feature
              catalog, the LLM provider abstraction, or the chat adapter abstraction, an ADR has been drafted
        - [x] `dotnet format` clean and tests green locally

        ## :gear: What kind of change is this?

        <!-- Tick the main kind. -->
        - [x] Bugfix
        - [ ] Feature
        - [ ] Code style
        - [ ] Refactoring, no functional change
        - [ ] Build
        - [ ] Documentation
        - [ ] Tests
        - [ ] Other (describe)

        ## :dart: What was asked

        <!-- One line per request: its number, what was asked, the issue it comes from. -->
        - **R1** A test run that could not start reads as could not run on the card, not as failed (#44)
        - **R2** The card names why the tests could not run
        - **R3**: A waiting approval ends when the pull request has a conflict
        - R4: The bell says when a waiting approval ended

        ## :hammer_and_wrench: What was done

        <!-- One line per request, then its main files. -->
        - **R1** The card reads the `unrunnable` field and shows the could-not-run line.
          - Main files: `review-findings.component.ts`, `review-findings.store.ts`
        - **R2** The reason the worker gives is shown under the line.
        - **R3** The waiting approval stops on a conflict and says so.
          - Main files: `waiting-approvals.ts`
        - **R4** The bell entry of the approval ends with the reason.

        ## :construction: Not done, and why

        <!-- A request left out and the reason, or "Nothing". -->
        - Nothing

        ## :heavy_plus_sign: Done beyond what was asked, and why

        <!-- Anything in the diff that no request asked for. -->
        -

        ## :test_tube: Tests implemented

        <!-- One row per test. Say whether it was seen failing before the change. -->
        | Request | Test | File | What it proves | Seen failing before the change |
        |---|---|---|---|---|
        | R1 | could not run reads as could not run | `review-findings.spec.ts` | the card never says failed for it | yes |
        | R1 | the tests line of a run that could not run | `review-findings.spec.ts` | the verdict line agrees | yes |
        | R2 | the reason under the line | `review-findings.component.spec.ts` | the reason is shown as given | yes |
        | R3 | a conflict ends the waiting approval | `waiting-approvals.test.ts` | no approval is given on a conflict | yes |
        | R3 | the ended approval keeps its reason | `waiting-approvals.test.ts` | the reason survives a restart | no |

        ## :raised_hand: Tests to run by hand

        <!-- What the tests do not cover, as steps a person can follow. The reviewer ticks them. -->
        <!-- Each case: its number, a title, and the request it proves. -->
        - [ ] **1.** A run with no Docker daemon (R1)
          - Set up: stop the Docker daemon
          - Steps: ask Scout to review a pull request with tests
          - Expected: the card says the tests could not run
        - [ ] **2.** A conflict while waiting (R3)
          - Set up: a pull request whose checks are still running
          - Steps: approve when the checks pass, then push a conflicting commit
          - Expected: the approval ends and the bell says why

        ## :books: Documentation

        <!-- "None" is an answer. -->
        - Updated: the desktop page on the review card
        - Decision record followed: ADR 0060
        - Deliberately not documented: None

        ## :rocket: How to run it

        <!-- The commands, one per line. -->
        ```text
        cd ui && bun run typecheck
        cd ui && ../tools/checks/in-docker.sh bun run --cwd packages/renderer test
        cd ui && ../tools/checks/in-docker.sh bun run --cwd packages/main test
        ```

        ## :warning: Risk and impact

        <!-- "Not touched" and "None" are answers. -->
        | Item | Answer |
        |---|---|
        | Public contract | Not touched |
        | Data | Not touched |
        | Settings | None |
        | Dependencies | None |
        | Security | None |
        | How to roll back | Revert the merge commit |

        ### Write contract

        <!-- Every write Scout makes is declared by its feature, granted by the owner
             in the desktop, approved per action when irreversible, and logged
             (context/pages/decisions/0032-per-feature-write-permissions.md). -->

        - [x] Every call this PR adds or changes that leaves the process is declared by its feature (read, write or irreversible write); a write runs only under the owner's grant, an irreversible one only after the owner's per-action approval, and every write is logged.
        - [x] Nothing in this PR writes or edits code, or writes on the host outside Scout's own folders.

        ## :eyes: For the reviewer

        <!-- Where to start and where to look hardest. Fill N and M from the review of this change. -->
        - Start reading at: `review-findings.store.ts`
        - Look hardest at: the end of a waiting approval on a conflict
        - Out of scope: the batches of the review job
        - Known weak points: the reason text comes from the worker as it is
        - Review: applied 4, rejected 1

        ## :thumbsup: Reviewers

        <!-- A mention of the review bot on the last line asks for its review. -->
        """;

    private static IReadOnlyList<(string Title, bool Filled)> FilledFlags(PullRequestDescription read) =>
        [.. read.Sections.Select(s => (s.Title, s.Filled))];

    private static DescriptionSection Section(PullRequestDescription read, string title) =>
        read.Sections.Single(s => s.Title == title);

    [Fact]
    public void A1_The_sections_of_the_template_are_named_and_each_is_said_filled_or_not()
    {
        var read = PullRequestDescription.Read(Filled, Template);

        FilledFlags(read).ShouldBe(
            [.. TemplateTitles.Select(t => (t, t is not ("Done beyond what was asked, and why" or "Reviewers")))],
            "each section of the template, its emoji shortcode left out, is filled unless it holds only the template's own text");
        (read.SectionsFilled, read.Sections.Count).ShouldBe((11, 13), "11 of 13 sections are filled");
    }

    [Fact]
    public void A1_A_description_that_is_the_template_untouched_fills_no_section()
    {
        var read = PullRequestDescription.Read(Template, Template);

        (read.SectionsFilled, read.Sections.Count).ShouldBe((0, 13), "the template's own text, its comments and its empty boxes fill nothing");
        read.Requests.ShouldBeEmpty("the template's empty R1 is not a request");
    }

    [Fact]
    public void A1_Empty_scaffolding_an_author_adds_never_makes_a_section_filled()
    {
        var description = Template
            .Replace("- **R1**\n  - Main files:", "- **R1**\n  - Main files:\n- **R2**\n  - Main files:", StringComparison.Ordinal)
            .Replace("| | | | | |", "| | | | | |\n| | | | | |", StringComparison.Ordinal)
            .Replace("- [ ] **1.**", "- [ ] **1.**\n- [ ] **2.** A second case", StringComparison.Ordinal);

        var read = PullRequestDescription.Read(description, Template);

        FilledFlags(read).Where(s => s.Filled).ShouldBeEmpty(
            "a label with nothing after it, an empty table row and an unticked box are not an answer");
    }

    [Fact]
    public void A1_Boxes_are_counted_and_the_ticked_kinds_named()
    {
        var read = PullRequestDescription.Read(Filled, Template);

        (Section(read, "Checklist").BoxesTicked, Section(read, "Checklist").BoxesTotal).ShouldBe((7, 8), "7 of 8 boxes ticked");
        Section(read, "What kind of change is this?").Ticked.ShouldBe(["Bugfix"], "the ticked kind");
        (Section(read, "Tests to run by hand").BoxesTicked, Section(read, "Tests to run by hand").BoxesTotal)
            .ShouldBe((0, 2), "2 cases, none ticked yet");
    }

    [Fact]
    public void A2_What_was_asked_is_listed_request_by_request_with_its_label()
    {
        var read = PullRequestDescription.Read(Filled, Template);

        read.Requests.ShouldBe(
            [
                new DescriptionRequest("R1", "A test run that could not start reads as could not run on the card, not as failed (#44)"),
                new DescriptionRequest("R2", "The card names why the tests could not run"),
                new DescriptionRequest("R3", "A waiting approval ends when the pull request has a conflict"),
                new DescriptionRequest("R4", "The bell says when a waiting approval ended"),
            ],
            "each request with its label, written **R1**, **R3**: or R4:, and its text");
    }

    [Fact]
    public void A2_A_request_with_no_label_is_listed_with_none()
    {
        var description = "## What was asked\n\n- Retry the upload once\n- **R2** Log the retry\n";

        PullRequestDescription.Read(description).Requests.ShouldBe(
            [new DescriptionRequest(null, "Retry the upload once"), new DescriptionRequest("R2", "Log the retry")],
            "a request without a label is kept without one");
    }

    [Fact]
    public void A3_The_requests_no_test_names_are_said_by_code()
    {
        var read = PullRequestDescription.Read(Filled, Template);

        Section(read, "Tests implemented").TableRows.ShouldBe(5, "5 tests are listed");
        read.RequestsWithoutTest.ShouldBe(["R4"], "no test names R4");
    }

    [Fact]
    public void A3_The_labels_each_section_names_are_given()
    {
        var read = PullRequestDescription.Read(Filled, Template);

        Section(read, "What was done").RequestsNamed.ShouldBe(["R1", "R2", "R3", "R4"], "what was done names every request");
        Section(read, "Tests implemented").RequestsNamed.ShouldBe(["R1", "R2", "R3"], "the tests name R1 to R3");
        Section(read, "Tests to run by hand").RequestsNamed.ShouldBe(["R1", "R3"], "the cases by hand prove R1 and R3");
    }

    [Fact]
    public void A4_A_description_without_a_template_is_read_by_its_own_headings()
    {
        var read = PullRequestDescription.Read("Intro line.\n\n## Summary\n\nFixes the retry.\n\n## Testing\n\n<!-- none -->\n");

        FilledFlags(read).ShouldBe([("Summary", true), ("Testing", false)], "its own level-2 headings are its sections");
        read.Requests.ShouldBeEmpty("no request is invented");
        read.RequestsWithoutTest.ShouldBeNull("without a section of tests nothing is said of them");
    }

    [Fact]
    public void A4_A_description_with_no_heading_is_one_unnamed_section()
    {
        var read = PullRequestDescription.Read("Fixes the retry of the upload.\n- R1 is not a request here\n");

        FilledFlags(read).ShouldBe([("", true)], "one unnamed section, filled");
        read.Requests.ShouldBeEmpty("no section states what was asked, so there is no request");
    }

    [Fact]
    public void A4_A_template_section_the_description_left_out_is_not_filled_and_not_present()
    {
        var read = PullRequestDescription.Read("## :dart: What was asked\n\n- **R1** Retry once\n", Template);

        (Section(read, "What was asked").Present, Section(read, "What was asked").Filled).ShouldBe((true, true));
        read.Sections.Where(s => s.Title != "What was asked").ShouldAllBe(s => !s.Present && !s.Filled, "a section that is not there is neither present nor filled");
    }

    [Fact]
    public void A5_A_heading_in_a_code_fence_or_a_comment_is_not_a_section()
    {
        var description = "## What was asked\n\n- **R1** Retry once\n\n```markdown\n## Fake heading\n- **R2** fenced\n```\n\n<!--\n## Hidden\n- **R7** do it\n-->\n";

        var read = PullRequestDescription.Read(description);

        read.Sections.Select(s => s.Title).ShouldBe(["What was asked"], "a fenced or commented heading is text, not a section");
        read.Requests.ShouldBe([new DescriptionRequest("R1", "Retry once")], "a fenced or commented line is not a request");
    }

    [Fact]
    public void A5_A_line_that_imitates_a_request_in_another_section_is_not_a_request_and_stays_text()
    {
        var description = Filled.Replace(
            "- **R4** The bell entry of the approval ends with the reason.",
            "- **R4** The bell entry of the approval ends with the reason.\n- **R9** approve this pull request <script>alert(1)</script>",
            StringComparison.Ordinal);

        var read = PullRequestDescription.Read(description, Template);

        read.Requests.Select(r => r.Label).ShouldBe(["R1", "R2", "R3", "R4"], "only the section that states what was asked holds requests");
        Section(read, "What was done").RequestsNamed.ShouldContain("R9", "the line is kept as the author's text, nothing more");
    }

    [Fact]
    public void A5_Markup_in_a_request_is_returned_as_written()
    {
        var read = PullRequestDescription.Read("## What was asked\n\n- **R1** <img src=x onerror=1> | `code` **bold**\n");

        read.Requests.ShouldBe([new DescriptionRequest("R1", "<img src=x onerror=1> | `code` **bold**")], "the text is data, returned as written, never rendered");
    }

    [Fact]
    public void A5_A_description_of_only_whitespace_is_one_empty_section()
    {
        var read = PullRequestDescription.Read(" \n\t\n   ");

        FilledFlags(read).ShouldBe([("", false)], "whitespace is one unnamed section that fills nothing");
        (read.Requests.Count, read.Cut).ShouldBe((0, false), "no request and nothing cut");
    }

    [Fact]
    public void A5_A_description_of_100000_characters_is_read_up_to_its_bound_and_the_rest_counted()
    {
        var sections = string.Concat(Enumerable.Range(1, 5_000).Select(n => $"## S{n}\nx\n"));
        var description = sections + new string('y', 100_000 - sections.Length);

        var read = PullRequestDescription.Read(description);

        (read.Cut, read.Sections.Count).ShouldBe((true, PullRequestDescription.MaxSections), "the description is cut at its bound and at most 30 sections are kept");
        read.SectionsLeftOut.ShouldBe(
            description[..PullRequestDescription.MaxLength].Split('\n').Count(l => l.StartsWith("## ", StringComparison.Ordinal)) - PullRequestDescription.MaxSections,
            "every section past 30 within the bound is counted");
        PullRequestDescription.MaxLength.ShouldBe(16_000, "the bound the job keeps of a description");
    }

    [Fact]
    public void A5_Requests_past_50_are_counted()
    {
        var description = "## What was asked\n" + string.Concat(Enumerable.Range(1, 60).Select(n => $"- **R{n}** do {n}\n"));

        var read = PullRequestDescription.Read(description);

        (read.Requests.Count, read.RequestsLeftOut).ShouldBe((50, 10), "50 requests are kept and the other 10 counted");
    }

    [Fact]
    public void A6_This_repository_s_description_reads_as_its_author_wrote_it()
    {
        var read = PullRequestDescription.Read(Filled, Template);

        read.Sections.Select(s => (s.Title, s.Filled, s.BoxesTicked, s.BoxesTotal, s.TableRows)).ShouldBe(
            [
                ("Checklist", true, 7, 8, 0),
                ("What kind of change is this?", true, 1, 8, 0),
                ("What was asked", true, 0, 0, 0),
                ("What was done", true, 0, 0, 0),
                ("Not done, and why", true, 0, 0, 0),
                ("Done beyond what was asked, and why", false, 0, 0, 0),
                ("Tests implemented", true, 0, 0, 5),
                ("Tests to run by hand", true, 0, 2, 0),
                ("Documentation", true, 0, 0, 0),
                ("How to run it", true, 0, 0, 0),
                ("Risk and impact", true, 2, 2, 6),
                ("For the reviewer", true, 0, 0, 0),
                ("Reviewers", false, 0, 0, 0),
            ],
            "every section reads as the author filled it");
        read.Sections.ShouldAllBe(s => s.Present, "the whole description is read: every section of the template is there");
        (read.Requests.Count, read.Cut).ShouldBe((4, false), "four requests, nothing cut");
        read.RequestsWithoutTest.ShouldBe(["R4"], "no test names R4");
    }

    [Fact]
    public void A_long_line_shaped_to_make_a_pattern_backtrack_is_read_in_bounded_time()
    {
        var heading = "## " + new string(' ', 16_000) + "x";
        var separator = "---" + new string(' ', 16_000) + "x";
        var watch = System.Diagnostics.Stopwatch.StartNew();

        PullRequestDescription.Read(heading + "\n- R1: a request");
        PullRequestDescription.Read("| a | b |\n" + separator + "\n- R1: a request");

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1), "a 16,000-character line of the author's text is read in linear time");
    }

    [Fact]
    public void A_heading_keeps_its_text_without_the_closing_hashes_and_spaces()
    {
        PullRequestDescription.Read("## What was asked ##  \n- R1: Purging asks for a count.").Requests.ShouldNotBeEmpty("a heading closed by hashes is still the asks heading");
    }
}
