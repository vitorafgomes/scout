using System.Text.RegularExpressions;
using Scout.Plugin.GitHub.Abstractions;
using Scout.Review;

namespace Scout.Plugin.GitHub.Tests;

public sealed partial class ReviewNoteTests
{
    private const string Commit = "4f2a9c1d00e";
    private const string PathHash = "274f9a969eb9685c73f8eb8f0cdb78a687ae73e8969eea5670f248b45837b879";
    private const string Line12 = $"https://github.com/acme/api/pull/42/files#diff-{PathHash}R12";

    private static readonly Finding Blocking = new("a", "block", "Move has no lock", "src/Orders/Checkout.cs", 12, null, "Two moves lose an item.", "Take the lock.", "Every write takes the lock.");
    private static readonly Finding ToFix = new("b", "fix", "Retry never ends", "src/Orders/Checkout.cs", 30, null, "The loop has no bound.", "Bound it.", null);
    private static readonly Finding Optional = new("c", "nit", "Name the limit", "src/Orders/Checkout.cs", 41, null, "The name hides its unit.", "Rename it.", null);

    private static readonly PullRequestCheck[] FourChecks =
    [
        new("build", "completed", "success", "https://ci/1"),
        new("lint", "completed", "skipped", "https://ci/2"),
        new("tests", "completed", "failure", "https://ci/3"),
        new("deploy", "in_progress", null, string.Empty),
    ];

    private static readonly ReviewTests FailedRun = new(true, null, "dotnet-sdk:10.0", "dotnet test", 1, 410.6, null, "Integration tests need a database.");

    private static ReviewNoteInput Input(
        string verdict = ReviewNote.Approve,
        IReadOnlyList<Finding>? findings = null,
        IReadOnlyList<PullRequestCheck>? checks = null,
        ReviewTests? tests = null,
        int? read = null,
        int? of = null,
        IReadOnlyList<string>? notes = null,
        string? login = "scout-acme",
        bool bot = true,
        bool every = true) =>
        new("acme", "api", 42, verdict, Commit, findings ?? [], checks, tests, read, of, notes, login, bot, every);

    private static string Body(ReviewNoteInput input) => ReviewNote.Body(input).ShouldNotBeNull("a note this size fits");

    private static string[] Lines(ReviewNoteInput input) => Body(input).Split('\n');

    /// <summary>The section under <paramref name="heading"/>, its lines up to the next heading or the footer, or null without one.</summary>
    private static string? Section(ReviewNoteInput input, string heading)
    {
        var lines = Lines(input);
        var start = Array.IndexOf(lines, heading);
        if (start < 0)
        {
            return null;
        }

        var end = Array.FindIndex(lines, start + 1, l => l.StartsWith("#### ", StringComparison.Ordinal) || l == "---");
        return string.Join('\n', lines[(start + 1)..end]).Trim('\n');
    }

    [GeneratedRegex(@":[a-z_]+:(?=\s*(\||$))", RegexOptions.Multiline)]
    private static partial Regex LoneShortcode();

    [GeneratedRegex(@"\A<!-- scout-review [0-9a-f]{16} -->\z")]
    private static partial Regex MarkerLine();

    [Fact]
    public void A_Request_For_Changes_With_Everything_Measured_Is_The_Whole_Note_In_The_Artboards_Order()
    {
        var lines = Lines(Input(ReviewNote.RequestChanges, [Blocking, ToFix, Optional], FourChecks, FailedRun, 5, 7, ["2 files not read: no diff was given for it."]));

        string.Join('\n', lines[..^1]).ShouldBe(
            $"""
            ### :robot: Scout review: changes requested

            > :x: **Changes requested** · tests: failed · checks: 1 of 4 passed · findings: 1 blocking, 1 to fix, 1 optional

            Commit `4f2a9c1`

            #### :mag: What changed, by area

            5 of 7 changed files read.

            **Not read:** 2 files.

            - 2 files not read: no diff was given for it.

            #### :arrow_forward: Tests Scout ran

            | Item | Measured |
            |---|---|
            | Command | `dotnet test` |
            | Where | Isolated container, no network, image `dotnet-sdk:10.0` |
            | Result | :x: Failed |
            | Duration | 411 s |

            **Left out:**

            - Integration tests need a database.

            #### :vertical_traffic_light: Checks on the commit

            :white_check_mark: 1 passed, :x: 1 failed, :fast_forward: 1 skipped, of 4 on `4f2a9c1`.

            <details><summary>The 4 checks</summary>

            | Check | Result | Link |
            |---|---|---|
            | build | :white_check_mark: Passed | [job](https://ci/1) |
            | lint | :fast_forward: Skipped | [job](https://ci/2) |
            | tests | :x: Failed | [job](https://ci/3) |
            | deploy | :hourglass_flowing_sand: Running |  |

            </details>

            #### :triangular_flag_on_post: Findings

            1 blocking, 1 to fix, 1 optional. Each one is also a comment on its line.

            | Severity | Where | What is wrong | Rule it breaks |
            |---|---|---|---|
            | :no_entry: Blocking | [`src/Orders/Checkout.cs:12`]({Line12}) | Move has no lock | Every write takes the lock. |
            | :wrench: Fix | [`src/Orders/Checkout.cs:30`](https://github.com/acme/api/pull/42/files#diff-{PathHash}R30) | Retry never ends | None |
            | :bulb: Suggestion, optional | [`src/Orders/Checkout.cs:41`](https://github.com/acme/api/pull/42/files#diff-{PathHash}R41) | Name the limit | None, optional |

            #### :microscope: How this review was made

            | Item | Value |
            |---|---|
            | Commit | `4f2a9c1` |

            - **Scout cannot see:** how the application behaves when it runs, the secrets of the repository, a private dependency it could not restore.
            - **Measured by code:** counts, paths, lines, durations, commands and check results.
            - **Written by the AI source from what it read:** the findings.

            #### :arrow_right: What happens next

            Push a fix and mention @scout-acme again. Scout reviews the new commit from the start.

            ---

            Posted after the owner's approval in Scout Desktop · signed as @scout-acme

            """,
            "the note follows the artboard's layout, with the sections code measured, in its order");
        MarkerLine().IsMatch(lines[^1]).ShouldBeTrue("a verdict's note ends with its hidden marker");
    }

    [Fact]
    public void An_Approval_Is_Headed_Scout_Review_Approved() =>
        Lines(Input())[0].ShouldBe("### :robot: Scout review: approved", "an approval's heading");

    [Fact]
    public void A_Request_For_Changes_Is_Headed_Changes_Requested() =>
        Lines(Input(ReviewNote.RequestChanges))[0].ShouldBe("### :robot: Scout review: changes requested", "a request for changes' heading");

    [Fact]
    public void A_Commenting_Review_Is_Headed_Commented_Not_Approved() =>
        Lines(Input("comment"))[0].ShouldBe("### :robot: Scout review: commented, not approved", "a commenting review's heading");

    [Fact]
    public void The_Bar_Of_An_Approval_Starts_With_Its_Shortcode_And_Word() =>
        Lines(Input())[2].ShouldBe("> :white_check_mark: **Approved** · findings: 0 blocking, 0 to fix, 0 optional", "an approval's bar");

    [Fact]
    public void The_Bar_Of_A_Request_For_Changes_Starts_With_Its_Shortcode_And_Word() =>
        Lines(Input(ReviewNote.RequestChanges, [Blocking]))[2].ShouldBe("> :x: **Changes requested** · findings: 1 blocking, 0 to fix, 0 optional", "a request for changes' bar");

    [Fact]
    public void The_Bar_Of_A_Commenting_Review_Starts_With_Its_Shortcode_And_Word() =>
        Lines(Input("comment", [Optional]))[2].ShouldBe("> :speech_balloon: **Commented, not approved** · findings: 0 blocking, 0 to fix, 1 optional", "a commenting review's bar");

    [Fact]
    public void The_Bar_Says_How_The_Tests_Ended_And_Leaves_Them_Out_Without_A_Run()
    {
        string Bar(ReviewTests? tests) => Lines(Input(tests: tests))[2];

        new[]
        {
            Bar(FailedRun with { ExitCode = 0 }),
            Bar(FailedRun),
            Bar(FailedRun with { Unrunnable = "The tests could not run: they need a Docker daemon." }),
            Bar(new ReviewTests(false, "the local container is not granted", null, null, null, null, null, null)),
            Bar(null),
        }.ShouldBe(
        [
            "> :white_check_mark: **Approved** · tests: passed · findings: 0 blocking, 0 to fix, 0 optional",
            "> :white_check_mark: **Approved** · tests: failed · findings: 0 blocking, 0 to fix, 0 optional",
            "> :white_check_mark: **Approved** · tests: could not run · findings: 0 blocking, 0 to fix, 0 optional",
            "> :white_check_mark: **Approved** · findings: 0 blocking, 0 to fix, 0 optional",
            "> :white_check_mark: **Approved** · findings: 0 blocking, 0 to fix, 0 optional",
        ], "the bar says passed, failed or could not run for a kept run, and nothing of tests that did not run");
    }

    [Fact]
    public void The_Bar_Counts_The_Checks_That_Passed_Of_Those_Read()
    {
        new[] { Lines(Input(checks: FourChecks))[2], Lines(Input(checks: []))[2] }.ShouldBe(
        [
            "> :white_check_mark: **Approved** · checks: 1 of 4 passed · findings: 0 blocking, 0 to fix, 0 optional",
            "> :white_check_mark: **Approved** · findings: 0 blocking, 0 to fix, 0 optional",
        ], "the bar counts the checks that passed of those read, and says nothing of a commit with none");
    }

    [Fact]
    public void The_Bar_Counts_The_Findings_By_Severity() =>
        Lines(Input(ReviewNote.RequestChanges, [Blocking, Blocking with { Id = "d" }, ToFix, Optional]))[2]
            .ShouldBe("> :x: **Changes requested** · findings: 2 blocking, 1 to fix, 1 optional", "block, fix and nit are counted as blocking, to fix and optional");

    [Fact]
    public void The_Commit_Line_Names_The_Commit_Reviewed() =>
        Lines(Input())[4].ShouldBe("Commit `4f2a9c1`", "the commit line names the commit reviewed, short");

    [Fact]
    public void What_Changed_Is_Written_Only_With_A_Measured_Count()
    {
        Section(Input(read: 3, of: 3), "#### :mag: What changed, by area").ShouldBe("3 of 3 changed files read.");
        Section(Input(read: 3), "#### :mag: What changed, by area").ShouldBeNull("without a measured count of files changed the section is left out");
        Section(Input(), "#### :mag: What changed, by area").ShouldBeNull("a review that measured nothing has no such section");
    }

    [Fact]
    public void What_Changed_Says_How_Many_Changed_Files_Were_Read() =>
        Section(Input(read: 51, of: 51), "#### :mag: What changed, by area").ShouldBe("51 of 51 changed files read.", "the files read of the files changed");

    [Fact]
    public void What_Changed_Says_How_Many_Files_Were_Not_Read() =>
        Section(Input(read: 51, of: 133), "#### :mag: What changed, by area")
            .ShouldBe("51 of 133 changed files read.\n\n**Not read:** 82 files.", "the files not read are counted when some were not");

    [Fact]
    public void What_Was_Not_Read_Is_Listed_In_Scouts_Own_Sentences() =>
        Section(Input(read: 51, of: 133, notes: ["80 files not read: the review was stopped.", "2 files not read: GitHub did not list these files."]), "#### :mag: What changed, by area")
            .ShouldBe(
                "51 of 133 changed files read.\n\n**Not read:** 82 files.\n\n- 80 files not read: the review was stopped.\n- 2 files not read: GitHub did not list these files.",
                "each of Scout's own sentences on what was not read is one bullet");

    [Fact]
    public void Tests_Scout_Ran_Is_Written_Only_With_A_Kept_Run()
    {
        Section(Input(), "#### :arrow_forward: Tests Scout ran").ShouldBeNull("without a kept run the section is left out");
        Section(Input(tests: FailedRun), "#### :arrow_forward: Tests Scout ran").ShouldNotBeNull();
    }

    [Fact]
    public void Tests_Scout_Ran_Is_A_Table_Of_Item_And_Measured() =>
        Section(Input(tests: FailedRun with { Note = null }), "#### :arrow_forward: Tests Scout ran").ShouldBe(
            "| Item | Measured |\n|---|---|\n| Command | `dotnet test` |\n| Where | Isolated container, no network, image `dotnet-sdk:10.0` |\n| Result | :x: Failed |\n| Duration | 411 s |",
            "a kept run is a table of item and measured value");

    [Fact]
    public void The_Command_That_Ran_Is_In_The_Table() =>
        Lines(Input(tests: FailedRun with { Command = "npm test -- --runInBand" })).ShouldContain("| Command | `npm test -- --runInBand` |", customMessage: "the command row");

    [Fact]
    public void The_Container_And_Its_Image_Are_In_The_Table()
    {
        Lines(Input(tests: FailedRun)).ShouldContain("| Where | Isolated container, no network, image `dotnet-sdk:10.0` |", customMessage: "the container and its image");
        Lines(Input(tests: FailedRun with { Image = null })).ShouldNotContain(l => l.StartsWith("| Where |", StringComparison.Ordinal), "without an image the row is left out");
    }

    [Fact]
    public void The_Result_Is_Its_Shortcode_And_Its_Word()
    {
        string Result(ReviewTests tests) => Lines(Input(tests: tests)).Single(l => l.StartsWith("| Result |", StringComparison.Ordinal));

        new[]
        {
            Result(FailedRun with { ExitCode = 0 }),
            Result(FailedRun),
            Result(FailedRun with { Unrunnable = "The tests could not run: they need a Docker daemon." }),
        }.ShouldBe(
        [
            "| Result | :white_check_mark: Passed |",
            "| Result | :x: Failed |",
            "| Result | The tests could not run: they need a Docker daemon. |",
        ], "a passed or failed run is its shortcode and its word; a run that could not run says why");
    }

    [Fact]
    public void The_Duration_Is_In_Whole_Seconds()
    {
        string Duration(double seconds) => Lines(Input(tests: FailedRun with { DurationSeconds = seconds })).Single(l => l.StartsWith("| Duration |", StringComparison.Ordinal));

        new[] { Duration(410.6), Duration(0.4), Duration(59.5) }.ShouldBe(["| Duration | 411 s |", "| Duration | 0 s |", "| Duration | 60 s |"], "the duration in whole seconds, rounded");
    }

    [Fact]
    public void What_The_Run_Left_Out_Is_Listed()
    {
        Section(Input(tests: FailedRun), "#### :arrow_forward: Tests Scout ran")!.ShouldEndWith("| Duration | 411 s |\n\n**Left out:**\n\n- Integration tests need a database.", customMessage: "the run's own note under Left out");
        Lines(Input(tests: FailedRun with { Note = " " })).ShouldNotContain("**Left out:**", "a run without a note leaves nothing out");
    }

    [Fact]
    public void Tests_That_Did_Not_Run_Say_Why_In_One_Line() =>
        Section(Input(tests: new ReviewTests(false, "the local container is not granted", null, null, null, null, null, null)), "#### :arrow_forward: Tests Scout ran")
            .ShouldBe("Not run: the local container is not granted.", "tests that did not run are one line with the kept reason");

    [Fact]
    public void Checks_On_The_Commit_Is_Written_When_The_Checks_Were_Read()
    {
        Section(Input(checks: FourChecks), "#### :vertical_traffic_light: Checks on the commit").ShouldNotBeNull();
        Section(Input(), "#### :vertical_traffic_light: Checks on the commit").ShouldBeNull("checks that were not read leave the section out");
    }

    [Fact]
    public void The_Checks_Sentence_Counts_Passed_Failed_And_Skipped()
    {
        PullRequestCheck[] checks =
        [
            new("build", "completed", "success", "https://ci/1"),
            new("docs", "completed", "neutral", "https://ci/2"),
            new("lint", "completed", "skipped", "https://ci/3"),
            new("tests", "completed", "timed_out", "https://ci/4"),
            new("e2e", "completed", "cancelled", "https://ci/5"),
        ];

        Section(Input(checks: checks), "#### :vertical_traffic_light: Checks on the commit")!.Split('\n')[0]
            .ShouldBe(":white_check_mark: 2 passed, :x: 2 failed, :fast_forward: 1 skipped, of 5 on `4f2a9c1`.", "success and neutral pass, skipped is skipped, any other conclusion failed");
    }

    [Theory]
    [InlineData("https://ci/1) and [more](https://evil", "[job](https://ci/1%29%20and%20[more]%28https://evil)")]
    [InlineData("https://ci/a|b", "[job](https://ci/a%7Cb)")]
    [InlineData("javascript:alert(1)", "")]
    [InlineData("ci/1", "")]
    public void A_Check_Address_Cannot_End_Its_Link_Or_Its_Cell(string url, string cell) =>
        Section(Input(checks: [new("build", "completed", "success", url)]), "#### :vertical_traffic_light: Checks on the commit")!
            .ShouldContain($"| build | :white_check_mark: Passed | {cell} |", customMessage: "only an http or https address is linked, with the characters that end a link or a cell encoded");

    [Fact]
    public void Each_Check_Is_A_Row_With_Its_Result_And_Link() =>
        Section(Input(checks: FourChecks), "#### :vertical_traffic_light: Checks on the commit")!.Split('\n')[2..].ShouldBe(
        [
            "<details><summary>The 4 checks</summary>",
            string.Empty,
            "| Check | Result | Link |",
            "|---|---|---|",
            "| build | :white_check_mark: Passed | [job](https://ci/1) |",
            "| lint | :fast_forward: Skipped | [job](https://ci/2) |",
            "| tests | :x: Failed | [job](https://ci/3) |",
            "| deploy | :hourglass_flowing_sand: Running |  |",
            string.Empty,
            "</details>",
        ], "each check is a row with its result and a link to its job when it has one");

    [Fact]
    public void A_Commit_With_No_Check_Says_So() =>
        Section(Input(checks: []), "#### :vertical_traffic_light: Checks on the commit").ShouldBe("No checks on this commit.", "a commit with no check says so");

    [Fact]
    public void Findings_Is_Always_Written()
    {
        Section(Input(), "#### :triangular_flag_on_post: Findings").ShouldNotBeNull("an approval with nothing found still has the section");
        Section(Input(ReviewNote.RequestChanges, [Blocking]), "#### :triangular_flag_on_post: Findings").ShouldNotBeNull();
    }

    [Fact]
    public void The_Findings_Sentence_Counts_Them_And_Says_Each_Is_A_Comment_Only_When_None_Was_Dropped()
    {
        Section(Input(ReviewNote.RequestChanges, [Blocking, Optional]), "#### :triangular_flag_on_post: Findings")!.Split('\n')[0]
            .ShouldBe("1 blocking, 0 to fix, 1 optional. Each one is also a comment on its line.", "every finding is a comment on its line");
        Section(Input(ReviewNote.RequestChanges, [Blocking, Optional], every: false), "#### :triangular_flag_on_post: Findings")!.Split('\n')[0]
            .ShouldBe("1 blocking, 0 to fix, 1 optional.", "when a comment was dropped the sentence does not say each one is");
    }

    [Fact]
    public void Each_Finding_Is_A_Row_With_A_Link_To_Its_Line() =>
        Section(Input(ReviewNote.RequestChanges, [Blocking]), "#### :triangular_flag_on_post: Findings")!.Split('\n')[2..].ShouldBe(
        [
            "| Severity | Where | What is wrong | Rule it breaks |",
            "|---|---|---|---|",
            $"| :no_entry: Blocking | [`src/Orders/Checkout.cs:12`]({Line12}) | Move has no lock | Every write takes the lock. |",
        ], "a finding is a row linking to its line in the pull request's diff");

    [Fact]
    public void A_Review_With_No_Finding_Says_None() =>
        Section(Input(), "#### :triangular_flag_on_post: Findings").ShouldBe("None.", "no finding is the one word None.");

    [Fact]
    public void A_Blocking_Finding_And_A_Fix_Carry_Their_Shortcode_And_Word() =>
        Section(Input(ReviewNote.RequestChanges, [Blocking, ToFix]), "#### :triangular_flag_on_post: Findings")!.Split('\n')[4..]
            .Select(r => r.Split(" | ")[0]).ShouldBe(["| :no_entry: Blocking", "| :wrench: Fix"], "blocking and fix carry their shortcode and word");

    [Fact]
    public void A_Suggestion_Is_Marked_Optional() =>
        Section(Input("comment", [Optional]), "#### :triangular_flag_on_post: Findings")!.Split('\n')[4]
            .ShouldStartWith("| :bulb: Suggestion, optional | ", customMessage: "a suggestion is marked optional");

    [Fact]
    public void A_Suggestion_Without_A_Rule_Reads_None_Optional() =>
        Section(Input("comment", [Optional, ToFix]), "#### :triangular_flag_on_post: Findings")!.Split('\n')[4..]
            .Select(r => r.Split(" | ")[^1]).ShouldBe(["None, optional |", "None |"], "a suggestion with no rule reads None, optional, a fix with none reads None");

    [Fact]
    public void A_Cell_Keeps_A_Pipe_And_A_Line_Break_From_Breaking_The_Table() =>
        Section(Input(ReviewNote.RequestChanges, [Blocking with { Title = "a | b\nc", Rule = "x\r\ny" }]), "#### :triangular_flag_on_post: Findings")!.Split('\n')[4]
            .ShouldBe($"| :no_entry: Blocking | [`src/Orders/Checkout.cs:12`]({Line12}) | a \\| b c | x y |", "a pipe is escaped and a line break becomes a space");

    [Fact]
    public void How_This_Review_Was_Made_Is_Always_Written()
    {
        Section(Input(), "#### :microscope: How this review was made").ShouldNotBeNull();
        Section(Input("comment", [Optional], FourChecks, FailedRun, 1, 2), "#### :microscope: How this review was made").ShouldNotBeNull();
    }

    [Fact]
    public void How_This_Review_Was_Made_Names_The_Commit() =>
        Section(Input(), "#### :microscope: How this review was made")!.Split('\n')[..3]
            .ShouldBe(["| Item | Value |", "|---|---|", "| Commit | `4f2a9c1` |"], "the table names the commit reviewed");

    [Fact]
    public void The_Note_Says_What_Scout_Cannot_See() =>
        Lines(Input()).ShouldContain("- **Scout cannot see:** how the application behaves when it runs, the secrets of the repository, a private dependency it could not restore.");

    [Fact]
    public void The_Note_Says_What_Code_Measured() =>
        Lines(Input()).ShouldContain("- **Measured by code:** counts, paths, lines, durations, commands and check results.");

    [Fact]
    public void The_Note_Says_What_The_AI_Source_Wrote() =>
        Lines(Input()).ShouldContain("- **Written by the AI source from what it read:** the findings.");

    [Fact]
    public void What_Happens_Next_Is_Written_For_Each_Verdict()
    {
        foreach (var verdict in new[] { ReviewNote.Approve, ReviewNote.RequestChanges, "comment" })
        {
            Section(Input(verdict), "#### :arrow_right: What happens next").ShouldNotBeNull($"a {verdict} signed by the bot says what happens next");
        }
    }

    [Fact]
    public void After_An_Approval_Nothing_Is_Asked_Of_The_Author()
    {
        Section(Input(), "#### :arrow_right: What happens next")
            .ShouldBe("Nothing is asked of the author. A new commit makes this approval stale: mention @scout-acme again.");
        Section(Input(login: "octocat", bot: false), "#### :arrow_right: What happens next")
            .ShouldBe("Nothing is asked of the author. A new commit makes this approval stale.", "the mention is dropped when no bot signs");
    }

    [Fact]
    public void After_A_Request_For_Changes_The_Author_Pushes_A_Fix_And_Mentions_The_Bot()
    {
        Section(Input(ReviewNote.RequestChanges), "#### :arrow_right: What happens next")
            .ShouldBe("Push a fix and mention @scout-acme again. Scout reviews the new commit from the start.");
        Section(Input(ReviewNote.RequestChanges, login: "octocat", bot: false), "#### :arrow_right: What happens next")
            .ShouldBeNull("without a bot to mention the section is left out");
    }

    [Fact]
    public void After_Suggestions_A_Person_Approves()
    {
        Section(Input("comment"), "#### :arrow_right: What happens next")
            .ShouldBe("Apply the suggestions you agree with, or leave them. Scout did not approve: a person approves, or mention @scout-acme after a new commit.");
        Section(Input("comment", login: "octocat", bot: false), "#### :arrow_right: What happens next")
            .ShouldBe("Apply the suggestions you agree with, or leave them. Scout did not approve: a person approves.", "the mention is dropped when no bot signs");
    }

    [Fact]
    public void The_Footer_Says_It_Was_Posted_After_The_Owners_Approval_And_Who_Signed()
    {
        var lines = Lines(Input("comment"));
        lines[^3..].ShouldBe(["---", string.Empty, "Posted after the owner's approval in Scout Desktop · signed as @scout-acme"], "the footer names who signed");
        Lines(Input("comment", login: null))[^1].ShouldBe("Posted after the owner's approval in Scout Desktop", "an unknown signer is left out");
    }

    [Fact]
    public void Only_A_Verdict_Ends_With_Its_Marker()
    {
        MarkerLine().IsMatch(Lines(Input())[^1]).ShouldBeTrue("an approval ends with its marker");
        MarkerLine().IsMatch(Lines(Input(ReviewNote.RequestChanges, [Blocking]))[^1]).ShouldBeTrue("a request for changes ends with its marker");
        Body(Input("comment")).ShouldNotContain("scout-review", customMessage: "a commenting review carries no marker");
    }

    [Fact]
    public void The_Digest_Of_The_Same_Verdict_Is_The_Same_Whatever_Type_Holds_Its_Findings()
    {
        var digest = ReviewNote.Digest(ReviewNote.RequestChanges, [Blocking, ToFix, Optional], 5, 7, ["Two files were not read."]);

        digest.ShouldBe("c84141b64f57c53e", "the digest recorded for this verdict, its findings, the counts and the note stays the same");
    }

    [Fact]
    public void The_Digest_Changes_With_The_Verdict_The_Findings_And_Each_Extra_And_Not_Otherwise()
    {
        string[] notes = ["2 files not read: no diff was given for it."];
        var digest = ReviewNote.Digest(ReviewNote.Approve, [Blocking], 5, 7, notes);

        digest.ShouldMatch("^[0-9a-f]{16}$");
        ReviewNote.Digest(ReviewNote.Approve, [Blocking], 5, 7, [.. notes]).ShouldBe(digest, "the same verdict, findings and extras give the same digest");
        ReviewNote.Digest(ReviewNote.RequestChanges, [Blocking], 5, 7, notes).ShouldNotBe(digest, "another verdict gives another digest");
        ReviewNote.Digest(ReviewNote.Approve, [Blocking with { Why = "Other." }], 5, 7, notes).ShouldNotBe(digest, "other findings give another digest");
        ReviewNote.Digest(ReviewNote.Approve, [Blocking], 6, 7, notes).ShouldNotBe(digest, "another count of files read gives another digest");
        ReviewNote.Digest(ReviewNote.Approve, [Blocking], 5, 8, notes).ShouldNotBe(digest, "another count of files changed gives another digest");
        ReviewNote.Digest(ReviewNote.Approve, [Blocking], 5, 7, ["Other note."]).ShouldNotBe(digest, "other notes give another digest");
        ReviewNote.Marker("0123456789abcdef").ShouldBe("<!-- scout-review 0123456789abcdef -->");
    }

    [Fact]
    public void No_Result_Is_Written_As_A_Shortcode_Alone()
    {
        var body = Body(Input(ReviewNote.RequestChanges, [Blocking, ToFix, Optional], FourChecks, FailedRun, 5, 7, ["2 files not read: no diff was given for it."]));

        LoneShortcode().Matches(body).ShouldBeEmpty("every shortcode is followed by its word");
    }

    [Fact]
    public void A_Note_Over_The_Limit_Drops_The_Checks_Table_Then_The_Notes()
    {
        var checks = Enumerable.Range(1, 600).Select(i => new PullRequestCheck($"check-{i:000}-{new string('c', 60)}", "completed", "success", $"https://ci/{i}")).ToList();
        var notes = Enumerable.Range(1, 200).Select(i => $"{i} {new string('n', 280)}").ToList();
        var findings = Enumerable.Range(1, 50).Select(i => Blocking with { Id = $"f{i}", Title = new string('t', 200) }).ToList();

        var withoutTable = Body(Input(ReviewNote.RequestChanges, findings, checks, read: 1, of: 9, notes: notes[..60]));
        var withoutNotes = Body(Input(ReviewNote.RequestChanges, findings, checks, read: 1, of: 9, notes: notes));
        var refused = ReviewNote.Body(Input(ReviewNote.RequestChanges, findings, checks, read: 1, of: 9, notes: [.. notes, .. notes, .. notes]) with
        {
            Findings = [.. findings.Select(f => f with { Title = new string('t', 1000) })],
        });

        (withoutTable.Length <= ReviewNote.MaxBodyLength, withoutTable.Contains("<details>", StringComparison.Ordinal), withoutTable.Contains("- 60 nnn", StringComparison.Ordinal))
            .ShouldBe((true, false, true), "over the limit the checks' table goes first and the notes stay");
        (withoutNotes.Length <= ReviewNote.MaxBodyLength, withoutNotes.Contains("<details>", StringComparison.Ordinal), withoutNotes.Contains("- 1 nnn", StringComparison.Ordinal), withoutNotes.Contains("**Not read:** 8 files.", StringComparison.Ordinal))
            .ShouldBe((true, false, false, true), "still over, the notes go and the count of files not read stays");
        refused.ShouldBeNull("a note that cannot fit even without both is refused");
    }

    [Fact]
    public void A_Path_And_A_Title_With_A_Backtick_A_Pipe_And_A_Line_Break_Stay_One_Row_Of_Four_Cells()
    {
        var odd = new Finding("o", "block", "Title `with| a\nbreak", "src/a`b|c\nd.cs", 12, null, "Why.", "Fix.", null);

        var row = Lines(Input(ReviewNote.RequestChanges, [odd])).Single(l => l.StartsWith("| :no_entry: Blocking", StringComparison.Ordinal));
        var cells = UnescapedPipe().Split(row.Trim('|'));

        cells.Length.ShouldBe(4, $"the finding's row stays one row of four cells: {row}");
        cells[1].Count(c => c == '`').ShouldBe(2, "the path's code span holds no backtick of its own, so it is not cut short");
        cells[1].ShouldContain("src/a'b\\|c d.cs", Case.Sensitive, "the backtick becomes an apostrophe, the pipe is escaped and the line break is a space");
    }

    [GeneratedRegex(@"(?<!\\)\|")]
    private static partial Regex UnescapedPipe();
}
