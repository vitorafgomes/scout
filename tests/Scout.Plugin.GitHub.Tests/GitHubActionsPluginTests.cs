using Microsoft.Extensions.Time.Testing;
using NSubstitute.ExceptionExtensions;
using Scout.Llm.Abstractions;
using Scout.Plugin.GitHub.Abstractions;
using Scout.Plugin.GitHub.Tests.Fakes;

namespace Scout.Plugin.GitHub.Tests;

public sealed class GitHubActionsPluginTests : IDisposable
{
    private const string Patch = "@@ -1,3 +1,4 @@\n a\n-b\n+c\n+d\n e";
    private static readonly string[] ExpectedLabels = ["ci", "tests"];

    private readonly string _dir = Directory.CreateTempSubdirectory("scout-github-actions-").FullName;
    private readonly IGitHubClient _reads = Substitute.For<IGitHubClient>();
    private readonly IGitHubWriteClient _writes = Substitute.For<IGitHubWriteClient>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero));

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private readonly IRepositoryProjects _projects = Substitute.For<IRepositoryProjects>();

    private GitHubProfileStore Profiles => new(Path.Combine(_dir, "profiles"));

    private RepositorySnapshotStore Snapshots(long maxBytes = 0) => new(Path.Combine(_dir, "snapshots"), maxBytes, _time);

    private GitHubActionsPlugin Plugin(long snapshotMaxBytes = 0) => new(_reads, _writes, Profiles, Snapshots(snapshotMaxBytes), _projects, _time);

    private static readonly RepositoryCommit MainCommit = new("main", "abc1234def5678", IsDefaultBranch: true);

    private void GitHubServesTheTarball(byte[]? tarGz = null)
    {
        _reads.ResolveCommitAsync("octocat", "hello", Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(MainCommit);
        _reads.DownloadTarballAsync("octocat", "hello", MainCommit.Sha, Arg.Any<Func<Stream, CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var consume = call.ArgAt<Func<Stream, CancellationToken, Task>>(3);
                await consume(new MemoryStream(tarGz ?? TarGzFixture.Build(TarGzFixture.Text("src/A.cs", "class A { }\n"), TarGzFixture.Text("README.md", "# A\n"))), CancellationToken.None);
                return true;
            });
    }

    private void PullRequestHasFiles() =>
        _reads.ListPullRequestFilesAsync("octocat", "hello", 42, Arg.Any<CancellationToken>())
            .Returns(new List<ChangedFile> { new("src/Checkout.cs", "modified", 2, 1, Patch), new("img/logo.png", "added", 0, 0, null) });

    private void GitHubAcceptsTheReview() =>
        _writes.SubmitReviewAsync("octocat", "hello", 42, Arg.Any<string>(), Arg.Any<IReadOnlyList<ReviewComment>>(), Arg.Any<CancellationToken>())
            .Returns(new PostedReview(77, "https://github.com/octocat/hello/pull/42#pullrequestreview-77"));

    [Fact]
    public async Task A_Review_Is_Posted_At_Once_With_The_Comments_On_The_Diff_And_The_Answer_Carries_The_Url_And_The_Dropped_Ones()
    {
        PullRequestHasFiles();
        GitHubAcceptsTheReview();

        var result = await Plugin().SubmitPullRequestReviewAsync("octocat", "hello", 42, "Ready after one fix.", """
            [{"path":"src/Checkout.cs","line":3,"body":"c is new"},
             {"path":"src/Checkout.cs","line":5,"body":"past the hunk"},
             {"path":"img/logo.png","line":1,"body":"binary"},
             {"path":"src/Other.cs","line":1,"body":"not in the PR"}]
            """, CancellationToken.None);

        result.ShouldBe("Posted a review on octocat/hello#42 with 1 comment(s): https://github.com/octocat/hello/pull/42#pullrequestreview-77 Dropped, not on a line of the diff: src/Checkout.cs:5, img/logo.png:1, src/Other.cs:1.");
        await _writes.Received(1).SubmitReviewAsync(
            "octocat", "hello", 42, "Ready after one fix.",
            Arg.Is<IReadOnlyList<ReviewComment>>(c => c.Count == 1 && c[0] == new ReviewComment("src/Checkout.cs", 3, "c is new")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Nothing_To_Post_Malformed_Comments_A_Missing_Pull_Request_And_A_Bad_Name_Are_Refused_Before_Anything_Is_Sent()
    {
        PullRequestHasFiles();
        _reads.ListPullRequestFilesAsync("octocat", "hello", 9999, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<ChangedFile>?)null);
        var plugin = Plugin();

        (await Refused(() => plugin.SubmitPullRequestReviewAsync("octocat", "hello", 42, "", """[{"path":"src/Checkout.cs","line":9,"body":"past the hunk"}]""", CancellationToken.None)))
            .ShouldBe(GitHubActionsPlugin.NothingToPost);
        (await Refused(() => plugin.SubmitPullRequestReviewAsync("octocat", "hello", 42, "b", "not json", CancellationToken.None)))
            .ShouldBe(GitHubActionsPlugin.InvalidComments);
        (await Refused(() => plugin.SubmitPullRequestReviewAsync("octocat", "hello", 42, "b", """[{"path":"a.cs","line":0,"body":"x"}]""", CancellationToken.None)))
            .ShouldBe(GitHubActionsPlugin.InvalidComments);
        (await Refused(() => plugin.SubmitPullRequestReviewAsync("octocat", "hello", 9999, "b", "[]", CancellationToken.None)))
            .ShouldBe("Pull request #9999 not found in octocat/hello or not accessible.");
        (await Refused(() => plugin.SubmitPullRequestReviewAsync("octo cat", "hello", 42, "b", "[]", CancellationToken.None)))
            .ShouldBe(GitHubNames.InvalidRepository);
        await _writes.DidNotReceiveWithAnyArgs().SubmitReviewAsync(default!, default!, default, default!, default!, default);
    }

    /// <summary>A refusal before anything is sent: the message for the model, the outcome "refused" for the write log.</summary>
    private static async Task<string> Refused(Func<Task<string>> call)
    {
        var ex = await Should.ThrowAsync<WriteFailedException>(call);
        ex.Outcome.ShouldBe("refused");
        return ex.Message;
    }

    [Fact]
    public async Task A_Line_Number_Written_As_A_String_Is_Read()
    {
        PullRequestHasFiles();
        GitHubAcceptsTheReview();

        var result = await Plugin().SubmitPullRequestReviewAsync("octocat", "hello", 42, "", """[{"path":"src/Checkout.cs","line":"3","body":"c"}]""", CancellationToken.None);

        result.ShouldStartWith("Posted a review on octocat/hello#42 with 1 comment(s)");
    }

    [Fact]
    public async Task GitHub_Refusing_The_Review_Is_The_Sentence_For_The_Model_And_The_Status_For_The_Log()
    {
        PullRequestHasFiles();
        _writes.SubmitReviewAsync(default!, default!, default, default!, default!, default)
            .ThrowsAsyncForAnyArgs(new GitHubApiException("Resource not accessible by integration", System.Net.HttpStatusCode.Forbidden));

        var ex = await Should.ThrowAsync<WriteFailedException>(() => Plugin().SubmitPullRequestReviewAsync("octocat", "hello", 42, "b", "[]", CancellationToken.None));

        ex.Message.ShouldBe("Resource not accessible by integration");
        ex.Outcome.ShouldBe("github 403");
    }

    [Fact]
    public async Task GitHub_Unreachable_After_The_Review_Was_Sent_Says_It_May_Already_Be_There()
    {
        PullRequestHasFiles();
        _writes.SubmitReviewAsync(default!, default!, default, default!, default!, default)
            .ThrowsAsyncForAnyArgs(new HttpRequestException("connection reset"));

        var ex = await Should.ThrowAsync<WriteFailedException>(() => Plugin().SubmitPullRequestReviewAsync("octocat", "hello", 42, "b", "[]", CancellationToken.None));

        ex.Message.ShouldBe("Could not reach GitHub. Try again later." + GitHubActionsPlugin.MayAlreadyBePosted);
        ex.Outcome.ShouldBe("HttpRequestException");
    }

    [Fact]
    public async Task GitHub_Unreachable_While_Reading_The_Diff_Never_Says_The_Review_May_Be_There()
    {
        _reads.ListPullRequestFilesAsync("octocat", "hello", 42, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("connection reset"));

        var ex = await Should.ThrowAsync<WriteFailedException>(() => Plugin().SubmitPullRequestReviewAsync("octocat", "hello", 42, "b", "[]", CancellationToken.None));

        ex.Message.ShouldBe("Could not reach GitHub. Try again later.");
        await _writes.DidNotReceiveWithAnyArgs().SubmitReviewAsync(default!, default!, default, default!, default!, default);
    }

    [Fact]
    public async Task A_Cancelled_Turn_Propagates_Cancellation_Instead_Of_An_Answer()
    {
        PullRequestHasFiles();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _writes.SubmitReviewAsync(default!, default!, default, default!, default!, default)
            .ThrowsAsyncForAnyArgs(new OperationCanceledException(cts.Token));

        await Should.ThrowAsync<OperationCanceledException>(() => Plugin().SubmitPullRequestReviewAsync("octocat", "hello", 42, "b", "[]", cts.Token));
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
    public async Task An_Issue_Is_Opened_At_Once_With_Its_Trimmed_Title_And_Distinct_Labels_And_The_Answer_Carries_Its_Number_And_Url()
    {
        _writes.CreateIssueAsync("octocat", "hello", "CI never runs the tests", "## What\n...", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new CreatedIssue(12, "https://github.com/octocat/hello/issues/12"));

        var result = await Plugin().CreateIssueAsync("octocat", "hello", " CI never runs the tests ", "## What\n...", "ci, tests, ci", CancellationToken.None);

        result.ShouldBe("Opened issue #12 on octocat/hello: https://github.com/octocat/hello/issues/12");
        await _writes.Received(1).CreateIssueAsync(
            "octocat", "hello", "CI never runs the tests", "## What\n...",
            Arg.Is<IReadOnlyList<string>>(l => l.SequenceEqual(ExpectedLabels)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_Issue_Without_A_Title_Or_A_Body_Is_Refused_Before_Anything_Is_Sent()
    {
        var plugin = Plugin();

        (await Refused(() => plugin.CreateIssueAsync("octocat", "hello", "", "body", "", CancellationToken.None))).ShouldBe("The issue title must be 1 to 256 characters.");
        (await Refused(() => plugin.CreateIssueAsync("octocat", "hello", "title", "  ", "", CancellationToken.None))).ShouldBe("The issue needs a body: what to change, why, how and where.");
        await _writes.DidNotReceiveWithAnyArgs().CreateIssueAsync(default!, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task A_Snapshot_Is_Taken_Extracted_Under_The_Data_Folder_Registered_As_A_Project_And_Found_Again_Without_A_Download()
    {
        GitHubServesTheTarball();
        var plugin = Plugin();

        var first = await plugin.SnapshotRepositoryAsync("octocat", "hello", "", CancellationToken.None);
        var second = await plugin.SnapshotRepositoryAsync("octocat", "hello", "main", CancellationToken.None);

        first.ShouldStartWith("Took a snapshot of octocat/hello at main (abc1234): 2 files");
        first.ShouldContain("search it as the project \"octocat/hello\"");
        second.ShouldStartWith("Already have a snapshot of octocat/hello at main (abc1234)");
        var root = Path.Combine(_dir, "snapshots", "octocat__hello", MainCommit.Sha);
        File.ReadAllText(Path.Combine(root, "src", "A.cs")).ShouldBe("class A { }\n");
        Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length.ShouldBe(2);
        _projects.Received(2).Register("octocat/hello", root);
        await _reads.Received(1).DownloadTarballAsync("octocat", "hello", MainCommit.Sha, Arg.Any<Func<Stream, CancellationToken, Task>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_Repository_That_Is_Missing_Over_The_Cap_Or_Without_A_Tarball_Is_Refused_And_Nothing_Is_Registered()
    {
        _reads.ResolveCommitAsync("octocat", "nope", Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns((RepositoryCommit?)null);
        GitHubServesTheTarball();
        _reads.DownloadTarballAsync("octocat", "gone", Arg.Any<string>(), Arg.Any<Func<Stream, CancellationToken, Task>>(), Arg.Any<CancellationToken>()).Returns(false);
        _reads.ResolveCommitAsync("octocat", "gone", Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(MainCommit);

        (await Refused(() => Plugin().SnapshotRepositoryAsync("octocat", "nope", "", CancellationToken.None))).ShouldBe("octocat/nope not found or not accessible.");
        (await Refused(() => Plugin(snapshotMaxBytes: 5).SnapshotRepositoryAsync("octocat", "hello", "", CancellationToken.None))).ShouldContain("over the snapshot cap");
        (await Refused(() => Plugin().SnapshotRepositoryAsync("octocat", "gone", "", CancellationToken.None))).ShouldBe("octocat/gone at main has no tarball GitHub can serve.");
        (await Refused(() => Plugin().SnapshotRepositoryAsync("octo cat", "hello", "", CancellationToken.None))).ShouldBe(GitHubNames.InvalidRepository);
        _projects.DidNotReceiveWithAnyArgs().Register(default!, default!);
    }

    [Fact]
    public async Task GitHub_Unreachable_During_The_Download_Is_The_Sentence_For_The_Model_And_Nothing_Is_Kept()
    {
        _reads.ResolveCommitAsync("octocat", "hello", Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(MainCommit);
        _reads.DownloadTarballAsync(default!, default!, default!, default!, default).ThrowsAsyncForAnyArgs(new HttpRequestException("connection reset"));

        var ex = await Should.ThrowAsync<WriteFailedException>(() => Plugin().SnapshotRepositoryAsync("octocat", "hello", "", CancellationToken.None));

        ex.Message.ShouldBe("Could not reach GitHub. Try again later.");
        ex.Outcome.ShouldBe("HttpRequestException");
        Directory.Exists(Path.Combine(_dir, "snapshots", "octocat__hello", MainCommit.Sha)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_Stream_That_Breaks_Mid_Download_Is_The_Sentence_For_The_Model_And_Nothing_Is_Kept()
    {
        _reads.ResolveCommitAsync("octocat", "hello", Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(MainCommit);
        _reads.DownloadTarballAsync("octocat", "hello", MainCommit.Sha, Arg.Any<Func<Stream, CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var consume = call.ArgAt<Func<Stream, CancellationToken, Task>>(3);
                await consume(new BreakingStream(TarGzFixture.Build(TarGzFixture.Text("src/A.cs", new string('x', 4096)))), CancellationToken.None);
                return true;
            });

        var ex = await Should.ThrowAsync<WriteFailedException>(() => Plugin().SnapshotRepositoryAsync("octocat", "hello", "", CancellationToken.None));

        ex.Message.ShouldBe("Could not reach GitHub. Try again later.");
        ex.Outcome.ShouldBe("IOException");
        Directory.Exists(Path.Combine(_dir, "snapshots", "octocat__hello")).ShouldBeTrue();
        Directory.GetDirectories(Path.Combine(_dir, "snapshots", "octocat__hello")).ShouldBeEmpty();
        _projects.DidNotReceiveWithAnyArgs().Register(default!, default!);
    }

    [Fact]
    public async Task The_Same_Commit_Reached_Through_Another_Reference_Is_Relabelled_Not_Downloaded_Again()
    {
        GitHubServesTheTarball();
        _reads.ResolveCommitAsync("octocat", "hello", "v1.2", Arg.Any<CancellationToken>()).Returns(new RepositoryCommit("v1.2", MainCommit.Sha, false));
        var plugin = Plugin();

        var tagged = await plugin.SnapshotRepositoryAsync("octocat", "hello", "v1.2", CancellationToken.None);
        var main = await plugin.SnapshotRepositoryAsync("octocat", "hello", "", CancellationToken.None);

        tagged.ShouldStartWith("Took a snapshot of octocat/hello at v1.2 (abc1234)");
        main.ShouldStartWith("Already have a snapshot of octocat/hello at main (abc1234)");
        Snapshots().Find("octocat", "hello", "").ShouldNotBeNull();
        await _reads.Received(1).DownloadTarballAsync("octocat", "hello", MainCommit.Sha, Arg.Any<Func<Stream, CancellationToken, Task>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A download whose connection drops after the first bytes.</summary>
    private sealed class BreakingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count) =>
            Position > 64 ? throw new IOException("connection reset") : base.Read(buffer, offset, Math.Min(count, 64));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Position > 64 ? throw new IOException("connection reset") : base.ReadAsync(buffer[..Math.Min(buffer.Length, 64)], cancellationToken);
    }
}
