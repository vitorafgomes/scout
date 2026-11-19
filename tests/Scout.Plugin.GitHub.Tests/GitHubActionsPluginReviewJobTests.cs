using Microsoft.Extensions.Time.Testing;
using NSubstitute.ExceptionExtensions;
using Scout.Plugin.GitHub.Abstractions;

namespace Scout.Plugin.GitHub.Tests;

public sealed class GitHubActionsPluginReviewJobTests : IDisposable
{
    private const string Head = "abc1234def5678";

    private readonly string _dir = Directory.CreateTempSubdirectory("scout-github-review-job-").FullName;
    private readonly IGitHubClient _reads = Substitute.For<IGitHubClient>();
    private readonly IGitHubWriteClient _writes = Substitute.For<IGitHubWriteClient>();
    private readonly IPullRequestReviews _reviews = Substitute.For<IPullRequestReviews>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero));

    private static readonly ChangedFile[] Files =
    [
        new("src/Big.cs", "modified", 1, 0, new string('+', GitHubPlugin.PatchBudget + 1)),
        new("src/Small.cs", "modified", 1, 0, "@@ -1 +1 @@\n+a"),
        new("assets/logo.png", "added", 0, 0, null),
    ];

    public GitHubActionsPluginReviewJobTests()
    {
        _reads.GetPullRequestAsync("octocat", "hello", 42, Arg.Any<CancellationToken>()).Returns(new PullRequestDetails(
            42, "Checkout", null, "alice", "open", false, "main", "feature", Head, 3, 0, "https://github.com/octocat/hello/pull/42", Files.Length, Files));
        _reads.ListCheckRunsAsync("octocat", "hello", Head, Arg.Any<CancellationToken>()).Returns(new CheckRuns(
        [
            new PullRequestCheck("build", "completed", "success", "https://ci/1"),
            new PullRequestCheck("lint", "completed", "skipped", "https://ci/2"),
            new PullRequestCheck("tests", "in_progress", null, "https://ci/3"),
        ], 3));
        _reads.ListStatusesAsync("octocat", "hello", Head, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<PullRequestCheck>)[new PullRequestCheck("deploy", "completed", "failure", "https://ci/4")]);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private GitHubActionsPlugin Plugin(bool withReviews = true) => new(
        _reads, _writes, new GitHubProfileStore(Path.Combine(_dir, "profiles")), new RepositorySnapshotStore(Path.Combine(_dir, "snapshots"), 0, _time),
        Substitute.For<IRepositoryProjects>(), _time, reviews: withReviews ? _reviews : null);

    [Fact]
    public async Task Start_passes_every_changed_file_the_head_and_the_left_out_count_and_relays_the_sentence()
    {
        _reviews.StartAsync("octocat", "hello", 42, Head, Arg.Any<IReadOnlyList<ChangedFile>>(), 1, Arg.Any<int>(), Arg.Any<PullRequestCheckCounts?>(), Arg.Any<CancellationToken>())
            .Returns("review started: 3 files, 2 batches, octocat/hello#42 at abc1234.");

        var answer = await Plugin().StartPullRequestReviewAsync("octocat", "hello", 42, CancellationToken.None);

        answer.ShouldBe("review started: 3 files, 2 batches, octocat/hello#42 at abc1234.", "the job's sentence is the answer");
        var call = _reviews.ReceivedCalls().ShouldHaveSingleItem().GetArguments();
        ((IReadOnlyList<ChangedFile>)call[4]!).ShouldBe(Files, "every changed file goes to the job with its patch, none dropped");
        ((int)call[5]!).ShouldBe(1, "the diffs left out are counted by get_pull_request's own budget rule");
        ((int)call[6]!).ShouldBe(0, "GitHub listed every changed file, so none is counted as not listed");
        ((PullRequestCheckCounts?)call[7]).ShouldBe(new PullRequestCheckCounts(2, 1, 1), "the head commit's checks as passing, pending and failing");
        _writes.ReceivedCalls().ShouldBeEmpty("starting a review job writes nothing on GitHub");
    }

    [Fact]
    public async Task A_pull_request_GitHub_lists_in_part_starts_a_review_that_counts_the_rest_as_not_read()
    {
        _reads.GetPullRequestAsync("octocat", "hello", 42, Arg.Any<CancellationToken>()).Returns(new PullRequestDetails(
            42, "Checkout", null, "alice", "open", false, "main", "feature", Head, 3, 0, "https://github.com/octocat/hello/pull/42", 3_250, Files));
        _reviews.StartAsync(default!, default!, default, default!, default!, default, default, default, default).ReturnsForAnyArgs("review started: 3250 files in 3 batches.");

        await Plugin().StartPullRequestReviewAsync("octocat", "hello", 42, CancellationToken.None);

        var call = _reviews.ReceivedCalls().ShouldHaveSingleItem().GetArguments();
        ((IReadOnlyList<ChangedFile>)call[4]!).ShouldBe(Files, "the files GitHub listed go to the job with their patches");
        ((int)call[6]!).ShouldBe(3_247, "the changed files GitHub counted but did not list go to the job, to be counted as not read");
    }

    [Fact]
    public async Task Start_relays_the_refusal_and_starts_nothing()
    {
        const string Refused = "GitHub writes are not allowed: the owner has not granted the GitHub feature's Scout's data folder.";
        _reviews.StartAsync(default!, default!, default, default!, default!, default, 0, default, default).ReturnsForAnyArgs(Refused);

        var answer = await Plugin().StartPullRequestReviewAsync("octocat", "hello", 42, CancellationToken.None);

        answer.ShouldBe(Refused, "a start the gate refused answers the gate's sentence");
        _reviews.ReceivedCalls().Count().ShouldBe(1, "the plugin asks once and starts nothing of its own");
        _writes.ReceivedCalls().ShouldBeEmpty("nothing is written on GitHub");
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("server error")]
    [InlineData("reset")]
    public async Task Start_says_GitHub_could_not_be_read_when_the_pull_request_read_fails(string failure)
    {
        Exception ex = failure switch
        {
            "timeout" => new TaskCanceledException("timed out"),
            "server error" => new GitHubApiException("GitHub is currently unavailable. Try again later.", System.Net.HttpStatusCode.BadGateway),
            _ => new HttpRequestException("connection reset"),
        };
        _reads.GetPullRequestAsync("octocat", "hello", 42, Arg.Any<CancellationToken>()).ThrowsAsync(ex);

        var answer = await Plugin().StartPullRequestReviewAsync("octocat", "hello", 42, CancellationToken.None);

        answer.ShouldBe($"GitHub could not be read ({ex.GetType().Name}); no review was started.", $"a {failure} on the read names the exception type only");
        _reviews.ReceivedCalls().ShouldBeEmpty($"a {failure} on the read starts no review");
    }

    [Fact]
    public async Task Start_of_a_pull_request_GitHub_does_not_find_says_so_and_starts_nothing()
    {
        var answer = await Plugin().StartPullRequestReviewAsync("octocat", "hello", 7, CancellationToken.None);

        answer.ShouldBe("Pull request #7 not found in octocat/hello or not accessible; no review was started.");
        _reviews.ReceivedCalls().ShouldBeEmpty("no review starts for a pull request GitHub does not find");
    }

    [Fact]
    public async Task Start_with_checks_that_cannot_be_read_passes_no_counts()
    {
        _reads.ListCheckRunsAsync("octocat", "hello", Head, Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("reset"));

        await Plugin().StartPullRequestReviewAsync("octocat", "hello", 42, CancellationToken.None);

        _reviews.ReceivedCalls().ShouldHaveSingleItem().GetArguments()[7].ShouldBeNull("checks that could not be read are no counts");
    }

    [Fact]
    public async Task Status_relays_the_rows_sentence()
    {
        _reviews.StatusAsync("octocat", "hello", 42, Arg.Any<CancellationToken>()).Returns("The review of octocat/hello#42 at abc1234 is running. 1 of 2 batches done.");

        var answer = await Plugin().GetPullRequestReviewStatusAsync("octocat", "hello", 42, CancellationToken.None);

        answer.ShouldBe("The review of octocat/hello#42 at abc1234 is running. 1 of 2 batches done.", "the status is the job's rows' sentence");
        _reads.ReceivedCalls().ShouldBeEmpty("the status reads Scout's own rows, never GitHub");
    }

    [Theory]
    [InlineData("no reviews", "octocat", 42, GitHubActionsPlugin.NoReviews)]
    [InlineData("bad name", "octo cat", 42, null)]
    [InlineData("bad number", "octocat", 0, GitHubActionsPlugin.InvalidPullRequestNumber)]
    public async Task Without_reviews_or_with_a_bad_name_or_number_both_functions_say_so_and_call_nothing(string input, string owner, int number, string? sentence)
    {
        var plugin = Plugin(withReviews: input != "no reviews");
        var expected = sentence ?? GitHubNames.InvalidRepository;

        (await plugin.StartPullRequestReviewAsync(owner, "hello", number, CancellationToken.None)).ShouldBe(expected, input);
        (await plugin.GetPullRequestReviewStatusAsync(owner, "hello", number, CancellationToken.None)).ShouldBe(expected, input);
        _reads.ReceivedCalls().ShouldBeEmpty($"{input}: GitHub is not read");
        _reviews.ReceivedCalls().ShouldBeEmpty($"{input}: no review is asked");
    }
}
