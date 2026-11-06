using Microsoft.Extensions.Time.Testing;
using Scout.Llm.Abstractions;
using Scout.Plugin.GitHub;
using Scout.Plugin.GitHub.Abstractions;
using Scout.Plugin.GitHub.Tests.Fakes;

namespace Scout.Plugin.GitHub.Tests;

public sealed class GitHubActionsPluginTests : IDisposable
{
    private const string Patch = "@@ -1,3 +1,4 @@\n a\n-b\n+c\n+d\n e";

    private readonly string _dir = Directory.CreateTempSubdirectory("scout-github-actions-").FullName;
    private readonly IGitHubClient _reads = Substitute.For<IGitHubClient>();
    private readonly RecordingWriteGate _gate = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero));

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private GitHubDraftStore Drafts => new(_dir);

    private GitHubProfileStore Profiles => new(Path.Combine(_dir, "profiles"));

    private GitHubActionsPlugin Plugin() => new(_reads, _gate, Drafts, Profiles, _time);

    private void PullRequestHasFiles() =>
        _reads.ListPullRequestFilesAsync("octocat", "hello", 42, Arg.Any<CancellationToken>())
            .Returns(new List<ChangedFile> { new("src/Checkout.cs", "modified", 2, 1, Patch), new("img/logo.png", "added", 0, 0, null) });

    [Fact]
    public async Task A_Review_Is_Saved_As_A_Draft_Queued_For_The_Owner_And_Keeps_Only_The_Comments_On_The_Diff()
    {
        PullRequestHasFiles();

        var result = await Plugin().SubmitPullRequestReviewAsync("octocat", "hello", 42, "Ready after one fix.", """
            [{"path":"src/Checkout.cs","line":3,"body":"c is new"},
             {"path":"src/Checkout.cs","line":5,"body":"past the hunk"},
             {"path":"img/logo.png","line":1,"body":"binary"},
             {"path":"src/Other.cs","line":1,"body":"not in the PR"}]
            """, CancellationToken.None);

        result.ShouldStartWith("Queued for the owner's approval on the Review screen of Scout Desktop (action act-1): 1 comment(s) on octocat/hello#42.");
        result.ShouldEndWith("Dropped, not on a line of the diff: src/Checkout.cs:5, img/logo.png:1, src/Other.cs:1.");
        var (feature, scope, target, summary) = _gate.Requests.ShouldHaveSingleItem();
        feature.ShouldBe("github");
        scope.ShouldBe("Pull request reviews");
        target.ShouldBe("octocat/hello#42");
        summary.ShouldBe("Post a review on octocat/hello#42: 1 comment(s); Ready after one fix.");
        var draft = (await Drafts.FindByActionAsync("act-1", CancellationToken.None)).ShouldNotBeNull();
        draft.Kind.ShouldBe("review");
        draft.Body.ShouldBe("Ready after one fix.");
        draft.Comments.ShouldBe([new ReviewComment("src/Checkout.cs", 3, "c is new")]);
        draft.CreatedAt.ShouldBe(_time.GetUtcNow());
    }

    [Fact]
    public async Task A_Refused_Review_Leaves_No_Draft_And_Relays_The_Reason()
    {
        PullRequestHasFiles();
        _gate.Decision = new WriteGateDecision(WriteGateDecisionKind.Refused, "The owner has not allowed github to write; allow it on the Features screen of Scout Desktop.", null);

        var result = await Plugin().SubmitPullRequestReviewAsync("octocat", "hello", 42, "b", """[{"path":"src/Checkout.cs","line":3,"body":"x"}]""", CancellationToken.None);

        result.ShouldBe("The owner has not allowed github to write; allow it on the Features screen of Scout Desktop.");
        Directory.GetFiles(_dir).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_Allowed_Decision_Is_A_Misconfiguration_Nothing_Runs_And_No_Draft_Stays()
    {
        PullRequestHasFiles();
        _gate.Decision = new WriteGateDecision(WriteGateDecisionKind.Allowed, string.Empty, null);

        var result = await Plugin().SubmitPullRequestReviewAsync("octocat", "hello", 42, "b", "[]", CancellationToken.None);

        result.ShouldBe(GitHubActionsPlugin.UnexpectedlyAllowed);
        Directory.GetFiles(_dir).ShouldBeEmpty();
    }

    [Fact]
    public async Task Nothing_To_Post_Malformed_Comments_And_A_Missing_Pull_Request_Are_Refused_Before_The_Gate()
    {
        PullRequestHasFiles();
        _reads.ListPullRequestFilesAsync("octocat", "hello", 9999, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<ChangedFile>?)null);
        var plugin = Plugin();

        (await plugin.SubmitPullRequestReviewAsync("octocat", "hello", 42, "", """[{"path":"src/Checkout.cs","line":9,"body":"past the hunk"}]""", CancellationToken.None))
            .ShouldBe(GitHubActionsPlugin.NothingToPost);
        (await plugin.SubmitPullRequestReviewAsync("octocat", "hello", 42, "b", "not json", CancellationToken.None))
            .ShouldBe(GitHubActionsPlugin.InvalidComments);
        (await plugin.SubmitPullRequestReviewAsync("octocat", "hello", 42, "b", """[{"path":"a.cs","line":0,"body":"x"}]""", CancellationToken.None))
            .ShouldBe(GitHubActionsPlugin.InvalidComments);
        (await plugin.SubmitPullRequestReviewAsync("octocat", "hello", 9999, "b", "[]", CancellationToken.None))
            .ShouldBe("Pull request #9999 not found in octocat/hello or not accessible.");
        (await plugin.SubmitPullRequestReviewAsync("octo cat", "hello", 42, "b", "[]", CancellationToken.None))
            .ShouldBe(GitHubNames.InvalidRepository);
        _gate.Requests.ShouldBeEmpty();
        Directory.GetFiles(_dir).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Line_Number_Written_As_A_String_Is_Read()
    {
        PullRequestHasFiles();

        var result = await Plugin().SubmitPullRequestReviewAsync("octocat", "hello", 42, "", """[{"path":"src/Checkout.cs","line":"3","body":"c"}]""", CancellationToken.None);

        result.ShouldStartWith("Queued for the owner's approval");
    }

    [Fact]
    public async Task A_Control_Plane_That_Does_Not_Answer_Leaves_No_Draft_And_Answers_A_Sentence()
    {
        PullRequestHasFiles();
        var gate = Substitute.For<IWriteGate>();
        gate.RequestAsync(default!, default!, default!, default!, default).ReturnsForAnyArgs<WriteGateDecision>(_ => throw new InvalidOperationException("socket gone"));

        var result = await new GitHubActionsPlugin(_reads, gate, Drafts, Profiles, _time)
            .SubmitPullRequestReviewAsync("octocat", "hello", 42, "b", "[]", CancellationToken.None);

        result.ShouldBe(GitHubActionsPlugin.ControlPlaneUnreachable + " (InvalidOperationException)");
        Directory.GetFiles(_dir).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Conventions_Profile_Is_Absent_Then_Saved_Then_Read_Back_With_Its_Date()
    {
        var plugin = Plugin();
        var profile = "## Error handling\n- Handlers return Result, never throw for expected outcomes (src/Orders/Checkout.cs)\n## Tests\n- xUnit + Shouldly (tests/Orders.Tests/Orders.Tests.csproj)";

        var before = await plugin.GetProjectConventionsAsync("octocat", "hello", CancellationToken.None);
        var saved = await plugin.RememberProjectConventionsAsync("octocat", "hello", profile, CancellationToken.None);
        var after = await plugin.GetProjectConventionsAsync("octocat", "hello", CancellationToken.None);

        before.ShouldBe("No conventions profile for octocat/hello yet. Discover the conventions from the project's own files and save them with remember_project_conventions.");
        saved.ShouldBe($"Saved the conventions profile of octocat/hello ({profile.Length} characters). Later audits read it with get_project_conventions.");
        after.ShouldBe("Conventions of octocat/hello, written 2026-11-06:\n" + profile);
        (await Profiles.FindAsync("octocat", "hello", CancellationToken.None)).ShouldNotBeNull().UpdatedAt.ShouldBe(_time.GetUtcNow());
        Directory.GetFiles(Path.Combine(_dir, "profiles"), "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Profile_Too_Short_Or_Too_Long_Or_For_An_Invalid_Repository_Is_Refused()
    {
        var plugin = Plugin();

        (await plugin.RememberProjectConventionsAsync("octocat", "hello", "too short", CancellationToken.None)).ShouldBe("The conventions profile must be 40 to 20000 characters.");
        (await plugin.RememberProjectConventionsAsync("octocat", "hello", new string('x', 20_001), CancellationToken.None)).ShouldBe("The conventions profile must be 40 to 20000 characters.");
        (await plugin.RememberProjectConventionsAsync("octo cat", "hello", new string('x', 100), CancellationToken.None)).ShouldBe(GitHubNames.InvalidRepository);
        (await plugin.GetProjectConventionsAsync("../x", "hello", CancellationToken.None)).ShouldBe(GitHubNames.InvalidRepository);
        Directory.Exists(Path.Combine(_dir, "profiles")).ShouldBeFalse();
    }

    [Fact]
    public async Task An_Issue_Is_Saved_As_A_Draft_And_Queued_With_Its_Title()
    {
        var result = await Plugin().CreateIssueAsync("octocat", "hello", " CI never runs the tests ", "## What\n...", "ci, tests, ci", CancellationToken.None);

        result.ShouldBe("Queued for the owner's approval on the Review screen of Scout Desktop (action act-1): the issue \"CI never runs the tests\" on octocat/hello. It is not posted yet; the chat hears when it is.");
        var (feature, scope, target, summary) = _gate.Requests.ShouldHaveSingleItem();
        (feature, scope, target).ShouldBe(("github", "New issues", "octocat/hello"));
        summary.ShouldBe("Open an issue on octocat/hello: CI never runs the tests");
        var draft = (await Drafts.FindByActionAsync("act-1", CancellationToken.None)).ShouldNotBeNull();
        draft.Kind.ShouldBe("issue");
        draft.Title.ShouldBe("CI never runs the tests");
        draft.Labels.ShouldBe(["ci", "tests"]);
    }

    [Fact]
    public async Task An_Issue_Without_A_Title_Or_A_Body_Is_Refused_Before_The_Gate()
    {
        var plugin = Plugin();

        (await plugin.CreateIssueAsync("octocat", "hello", "", "body", "", CancellationToken.None)).ShouldBe("The issue title must be 1 to 256 characters.");
        (await plugin.CreateIssueAsync("octocat", "hello", "title", "  ", "", CancellationToken.None)).ShouldBe("The issue needs a body: what to change, why, how and where.");
        _gate.Requests.ShouldBeEmpty();
    }
}
