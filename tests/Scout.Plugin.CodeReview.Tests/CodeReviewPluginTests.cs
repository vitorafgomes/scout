using Microsoft.Extensions.Options;
using Scout.Plugin.CodeReview.Abstractions;

namespace Scout.Plugin.CodeReview.Tests;

public sealed class CodeReviewPluginTests
{
    private static ICodeReviewer Reviewer(string reply = "review text")
    {
        var reviewer = Substitute.For<ICodeReviewer>();
        reviewer.ReviewAsync(Arg.Any<ReviewKind>(), Arg.Any<CodeReviewInput>(), Arg.Any<CancellationToken>())
            .Returns(reply);
        return reviewer;
    }

    private static CodeReviewPlugin Plugin(
        ICodeReviewer reviewer,
        IRepositoryFetcher? fetcher = null,
        CodeReviewOptions? options = null)
        => new(reviewer, fetcher, Options.Create(options ?? new CodeReviewOptions()));

    [Fact]
    public async Task AnalyzeCodeQuality_Returns_Reviewer_Text()
    {
        var result = await Plugin(Reviewer("quality result"))
            .AnalyzeCodeQualityAsync("var x = 1;", CancellationToken.None);

        result.ShouldBe("quality result");
    }

    [Fact]
    public async Task DetectVulnerabilities_Returns_Reviewer_Text()
    {
        var result = await Plugin(Reviewer("vuln result"))
            .DetectVulnerabilitiesAsync("eval(input)", CancellationToken.None);

        result.ShouldBe("vuln result");
    }

    [Fact]
    public async Task SuggestImprovements_Returns_Reviewer_Text()
    {
        var result = await Plugin(Reviewer("improve result"))
            .SuggestImprovementsAsync("code", CancellationToken.None);

        result.ShouldBe("improve result");
    }

    [Fact]
    public async Task GenerateTests_Returns_Reviewer_Text()
    {
        var result = await Plugin(Reviewer("tests result"))
            .GenerateTestsAsync("code", "xUnit", CancellationToken.None);

        result.ShouldBe("tests result");
    }

    [Fact]
    public async Task ExplainCode_Returns_Reviewer_Text()
    {
        var result = await Plugin(Reviewer("explain result"))
            .ExplainCodeAsync("code", "beginner", CancellationToken.None);

        result.ShouldBe("explain result");
    }

    [Fact]
    public async Task ReviewPullRequest_Returns_Reviewer_Text()
    {
        var result = await Plugin(Reviewer("pr result"))
            .ReviewPullRequestAsync("diff --git a b", CancellationToken.None);

        result.ShouldBe("pr result");
    }

    [Fact]
    public async Task ReviewPullRequest_Never_Touches_Fetcher()
    {
        var fetcher = Substitute.For<IRepositoryFetcher>();

        await Plugin(Reviewer(), fetcher)
            .ReviewPullRequestAsync("diff --git a b", CancellationToken.None);

        await fetcher.DidNotReceive().FetchAsync(
            Arg.Any<RepositoryCoordinates>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GenerateTests_Passes_Framework_Hint()
    {
        var reviewer = Reviewer();

        await Plugin(reviewer).GenerateTestsAsync("code", "pytest", CancellationToken.None);

        await reviewer.Received().ReviewAsync(
            ReviewKind.Tests,
            Arg.Is<CodeReviewInput>(i => i.Framework == "pytest" && i.Snippet == "code"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExplainCode_Defaults_Level_To_Intermediate()
    {
        var reviewer = Reviewer();

        await Plugin(reviewer).ExplainCodeAsync("code", ct: CancellationToken.None);

        await reviewer.Received().ReviewAsync(
            ReviewKind.Explain,
            Arg.Is<CodeReviewInput>(i => i.Level == "intermediate"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reviewer_LlmReviewException_Returns_Clean_Line_Without_Throwing()
    {
        var reviewer = Substitute.For<ICodeReviewer>();
        reviewer.ReviewAsync(Arg.Any<ReviewKind>(), Arg.Any<CodeReviewInput>(), Arg.Any<CancellationToken>())
            .Returns<string>(_ => throw new LlmReviewException("The model returned an empty review."));

        var plugin = Plugin(reviewer);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.AnalyzeCodeQualityAsync("code", CancellationToken.None));

        result.ShouldBe("The model returned an empty review.");
    }

    [Fact]
    public async Task Reviewer_Timeout_Returns_Clean_Line_Without_Throwing()
    {
        var reviewer = Substitute.For<ICodeReviewer>();
        reviewer.ReviewAsync(Arg.Any<ReviewKind>(), Arg.Any<CodeReviewInput>(), Arg.Any<CancellationToken>())
            .Returns<string>(_ => throw new TaskCanceledException("timeout"));

        var plugin = Plugin(reviewer);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.DetectVulnerabilitiesAsync("code", CancellationToken.None));

        result.ShouldBe("The review timed out. Try again later.");
    }

    [Fact]
    public async Task Reviewer_Transport_Failure_Returns_Clean_Line_Without_Throwing()
    {
        var reviewer = Substitute.For<ICodeReviewer>();
        reviewer.ReviewAsync(Arg.Any<ReviewKind>(), Arg.Any<CodeReviewInput>(), Arg.Any<CancellationToken>())
            .Returns<string>(_ => throw new HttpRequestException("connection reset"));

        var plugin = Plugin(reviewer);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.SuggestImprovementsAsync("code", CancellationToken.None));

        result.ShouldBe("Could not complete the review. Try again later.");
    }

    [Fact]
    public void Ctor_Throws_When_Reviewer_Null()
    {
        Should.Throw<ArgumentNullException>(() => new CodeReviewPlugin(null!));
    }

    [Fact]
    public async Task AnalyzeRepository_Null_Fetcher_Returns_Token_Required_Line()
    {
        var plugin = Plugin(
            Reviewer(),
            fetcher: null,
            new CodeReviewOptions { DefaultRepoUrl = "https://github.com/octocat/hello-world" });

        var result = await plugin.AnalyzeRepositoryAsync(ct: CancellationToken.None);

        result.ShouldBe(
            "Repository analysis requires a configured GitHub token. The snippet review functions still work without one.");
    }

    [Fact]
    public async Task AnalyzeRepository_Bad_Url_Returns_Parse_Error_Line()
    {
        var fetcher = Substitute.For<IRepositoryFetcher>();

        var result = await Plugin(Reviewer(), fetcher)
            .AnalyzeRepositoryAsync("not a url", CancellationToken.None);

        result.ShouldBe(
            "That does not look like a GitHub repository URL. Use https://github.com/owner/repo.");
        await fetcher.DidNotReceive().FetchAsync(
            Arg.Any<RepositoryCoordinates>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnalyzeRepository_No_Url_And_No_Default_Returns_Clean_Line()
    {
        var result = await Plugin(Reviewer())
            .AnalyzeRepositoryAsync(ct: CancellationToken.None);

        result.ShouldBe(
            "No repository URL was provided and no default is configured. Pass a GitHub repository URL.");
    }

    [Fact]
    public async Task AnalyzeRepository_Uses_Default_Url_When_None_Given()
    {
        var fetcher = Substitute.For<IRepositoryFetcher>();
        fetcher.FetchAsync(Arg.Any<RepositoryCoordinates>(), Arg.Any<CancellationToken>())
            .Returns(new RepositorySnapshot(
                "octocat/hello-world",
                "main",
                new List<RepositoryFile> { new("Program.cs", "class P {}") }));

        var plugin = Plugin(
            Reviewer("repo review"),
            fetcher,
            new CodeReviewOptions { DefaultRepoUrl = "https://github.com/octocat/hello-world" });

        var result = await plugin.AnalyzeRepositoryAsync(ct: CancellationToken.None);

        result.ShouldBe("repo review");
        await fetcher.Received().FetchAsync(
            Arg.Is<RepositoryCoordinates>(c => c.Owner == "octocat" && c.Repo == "hello-world"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnalyzeRepository_Empty_Snapshot_Returns_No_Files_Line_Without_Calling_Reviewer()
    {
        var fetcher = Substitute.For<IRepositoryFetcher>();
        fetcher.FetchAsync(Arg.Any<RepositoryCoordinates>(), Arg.Any<CancellationToken>())
            .Returns(new RepositorySnapshot("octocat/hello-world", "main", new List<RepositoryFile>()));
        var reviewer = Reviewer("repo review");

        var result = await Plugin(reviewer, fetcher)
            .AnalyzeRepositoryAsync("https://github.com/octocat/hello-world", CancellationToken.None);

        result.ShouldBe("No reviewable text files were found in octocat/hello-world.");
        await reviewer.DidNotReceive().ReviewAsync(
            Arg.Any<ReviewKind>(), Arg.Any<CodeReviewInput>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnalyzeRepository_Fetch_Failure_Returns_Clean_Line_Without_Throwing()
    {
        var fetcher = Substitute.For<IRepositoryFetcher>();
        fetcher.FetchAsync(Arg.Any<RepositoryCoordinates>(), Arg.Any<CancellationToken>())
            .Returns<RepositorySnapshot>(_ => throw new CodeReviewFetchException(
                "Repository not found or the token has no access."));

        var plugin = Plugin(Reviewer(), fetcher);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.AnalyzeRepositoryAsync(
                "https://github.com/octocat/ghost", CancellationToken.None));

        result.ShouldBe("Repository not found or the token has no access.");
    }
}
