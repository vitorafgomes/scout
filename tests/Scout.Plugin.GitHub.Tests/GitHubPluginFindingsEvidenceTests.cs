using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.SemanticKernel;
using NSubstitute.ExceptionExtensions;
using Scout.Llm.Abstractions;
using Scout.Plugin.GitHub.Abstractions;

namespace Scout.Plugin.GitHub.Tests;

public sealed partial class GitHubPluginFindingsEvidenceTests
{
    private const string Sha = "ec7f951aaaa0000000000000000000000000000a";
    private const string OneFinding = """[{"id":"total","severity":"block","title":"Discounts applied after the sum","path":"src/Checkout.cs","line":88,"why":"w","fix":"f"}]""";

    private readonly IGitHubClient _client = Substitute.For<IGitHubClient>();
    private readonly TestRuns _runs = new();

    [GeneratedRegex(@"```scout-findings\n(.*)\n```", RegexOptions.Singleline)]
    private static partial Regex Block();

    public GitHubPluginFindingsEvidenceTests()
    {
        _client.GetPullRequestHeadAsync("octocat", "hello", 5, Arg.Any<CancellationToken>()).Returns(new PullRequestHead(Sha, "alice"));
        _client.GetSignedInLoginAsync(Arg.Any<CancellationToken>()).Returns("octocat");
        _client.ListCheckRunsAsync("octocat", "hello", Sha, Arg.Any<CancellationToken>()).Returns(new CheckRuns(
            [new PullRequestCheck("build", "completed", "success", "https://github.com/r/1"), new PullRequestCheck("tests", "in_progress", null, "https://github.com/r/2")], 2));
        _client.ListStatusesAsync("octocat", "hello", Sha, Arg.Any<CancellationToken>())
            .Returns(new List<PullRequestCheck> { new("ci/lint", "completed", "failure", "https://ci.example/1") });
    }

    private async Task<(string Answer, JsonElement? Review)> PresentAsync(string commit = "ec7f951", BotSwitches? signing = null)
    {
        using var turn = TurnAttachments.Begin();
        var plugin = KernelPluginFactory.CreateFromObject(new GitHubPlugin(_client, null, signing, _runs), "GitHub");
        var arguments = new KernelArguments
        {
            ["repository"] = "octocat/hello",
            ["number"] = 5,
            ["verdict"] = "needs rework",
            ["findings_json"] = OneFinding,
            ["commit"] = commit,
        };
        var answer = (await plugin["present_review_findings"].InvokeAsync(new Kernel(), arguments)).GetValue<string>()!;
        var block = TurnAttachments.Take().SingleOrDefault();
        return (answer, block is null ? null : JsonDocument.Parse(Block().Match(block).Groups[1].Value).RootElement.Clone());
    }

    [Fact]
    public async Task The_Block_Carries_The_Checks_Scout_Read_Itself_At_The_Commit_Reviewed_With_A_Running_Check_s_Conclusion_As_Null()
    {
        var (_, review) = await PresentAsync();

        review.ShouldNotBeNull().GetProperty("checks").GetRawText().ShouldBe(
            """[{"name":"build","status":"completed","conclusion":"success","url":"https://github.com/r/1"},{"name":"tests","status":"in_progress","conclusion":null,"url":"https://github.com/r/2"},{"name":"ci/lint","status":"completed","conclusion":"failure","url":"https://ci.example/1"}]""");
    }

    [Fact]
    public async Task A_Pull_Request_That_Moved_Past_The_Commit_Reviewed_Gets_No_Checks_In_The_Block()
    {
        var (_, review) = await PresentAsync(commit: "fedcba9");

        review.ShouldNotBeNull().TryGetProperty("checks", out _).ShouldBeFalse();
        await _client.DidNotReceiveWithAnyArgs().ListCheckRunsAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task The_Block_Carries_The_Last_Container_Run_Kept_For_The_Commit_Reviewed()
    {
        _runs.Keep("octocat", "hello", Sha, new ReviewTests(true, null, "node@sha256:1", "npm test", 1, 12.5, "1 failing", "Ran on Node 22."));

        var (_, review) = await PresentAsync();

        review.ShouldNotBeNull().GetProperty("tests").GetRawText().ShouldBe(
            """{"ran":true,"image":"node@sha256:1","command":"npm test","exitCode":1,"durationSeconds":12.5,"output":"1 failing","note":"Ran on Node 22."}""");
    }

    [Fact]
    public async Task A_Run_Kept_For_Another_Commit_Or_Repository_Is_Not_Attached()
    {
        _runs.Keep("octocat", "hello", "0000000aaaa", new ReviewTests(true, null, "i", "c", 0, 1, "ok", null));
        _runs.Keep("octocat", "other", Sha, new ReviewTests(true, null, "i", "c", 0, 1, "ok", null));

        var (_, review) = await PresentAsync();

        review.ShouldNotBeNull().TryGetProperty("tests", out _).ShouldBeFalse();
    }

    [Fact]
    public void The_Runs_Kept_Are_The_Last_Twenty_And_A_New_Run_Of_A_Commit_Replaces_The_Old_One()
    {
        for (var i = 0; i < 21; i++)
        {
            _runs.Keep("octocat", "hello", string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{i:D7}aaaa"), new ReviewTests(true, null, null, null, i, null, null, null));
        }

        _runs.Keep("octocat", "hello", "0000020aaaa", new ReviewTests(false, "replaced", null, null, null, null, null, null));

        _runs.Find("octocat", "hello", "0000000").ShouldBeNull();
        _runs.Find("octocat", "hello", "0000001").ShouldNotBeNull().ExitCode.ShouldBe(1);
        _runs.Find("octocat", "hello", "0000020").ShouldNotBeNull().Reason.ShouldBe("replaced");
        _runs.Find("octocat", "hello", "000002").ShouldBeNull();
    }

    [Theory]
    [InlineData("alice", true)]
    [InlineData("ALICE", true)]
    [InlineData("octocat", false)]
    public async Task A_Pull_Request_The_Owner_Opened_Is_Marked_Self_Authored_So_The_Card_Offers_No_Approval(string signer, bool selfAuthored)
    {
        _client.GetSignedInLoginAsync(Arg.Any<CancellationToken>()).Returns(signer);

        var (_, review) = await PresentAsync();

        var block = review.ShouldNotBeNull();
        if (selfAuthored)
        {
            block.GetProperty("selfAuthored").GetBoolean().ShouldBeTrue();
        }
        else
        {
            block.TryGetProperty("selfAuthored", out _).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task When_The_Bot_Signs_Verdicts_A_Pull_Request_The_Owner_Opened_Is_Not_Self_Authored_But_One_The_Bot_Opened_Is()
    {
        var bot = BotSwitches.Parse("verdict", "octocat/hello", "scout-octocat[bot]");
        _client.GetSignedInLoginAsync(Arg.Any<CancellationToken>()).Returns("alice");

        var (_, ownersPullRequest) = await PresentAsync(signing: bot);
        _client.GetPullRequestHeadAsync("octocat", "hello", 5, Arg.Any<CancellationToken>()).Returns(new PullRequestHead(Sha, "scout-octocat[bot]"));
        var (_, botsPullRequest) = await PresentAsync(signing: bot);

        ownersPullRequest.ShouldNotBeNull().TryGetProperty("selfAuthored", out _).ShouldBeFalse();
        botsPullRequest.ShouldNotBeNull().GetProperty("selfAuthored").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task When_GitHub_Cannot_Say_Who_Opened_The_Pull_Request_Or_Its_Checks_The_Findings_Are_Still_Recorded()
    {
        _client.ListStatusesAsync("octocat", "hello", Sha, Arg.Any<CancellationToken>()).ThrowsAsync(new TaskCanceledException("timed out"));
        var (checksFailed, withoutChecks) = await PresentAsync();
        _client.GetPullRequestHeadAsync("octocat", "hello", 5, Arg.Any<CancellationToken>()).ThrowsAsync(new TaskCanceledException("timed out"));
        var (headFailed, withoutHead) = await PresentAsync();

        checksFailed.ShouldStartWith("Findings recorded: 1.");
        withoutChecks.ShouldNotBeNull().TryGetProperty("checks", out _).ShouldBeFalse();
        headFailed.ShouldStartWith("Findings recorded: 1.");
        withoutHead.ShouldNotBeNull().TryGetProperty("selfAuthored", out _).ShouldBeFalse();
    }
}
