using NSubstitute;
using Scout.Audit;
using Scout.Plugin.GitHub.Abstractions;
using Shouldly;
using Xunit;

namespace Scout.Host.Console.Tests;

public sealed class PullRequestReviewsAdapterTests
{
    [Fact]
    public async Task A_start_passes_the_count_of_files_GitHub_did_not_list_and_the_files_to_the_audit_library()
    {
        var reviews = Substitute.For<Scout.Audit.IPullRequestReviews>();
        reviews.StartAsync(default!, default!, default, default!, default!, default, default, default, default)
            .ReturnsForAnyArgs(new ReviewStart(true, "review started.", ReviewPreflight.None));
        ChangedFile[] files = [new("src/A.cs", "modified", 1, 0, "@@ -1 +1 @@\n+a"), new("assets/logo.png", "added", 0, 0, null)];

        var answer = await new PullRequestReviewsAdapter(reviews).StartAsync("acme", "shop", 12, "abc1234", files, 1, 2_999, null, CancellationToken.None);

        answer.ShouldBe("review started.");
        var call = reviews.ReceivedCalls().ShouldHaveSingleItem().GetArguments();
        ((int)call[6]!).ShouldBe(2_999, "the count of files GitHub did not list reaches the audit library unchanged");
        ((IReadOnlyList<ReviewFile>)call[4]!).ShouldBe([new ReviewFile("src/A.cs", "@@ -1 +1 @@\n+a"), new ReviewFile("assets/logo.png", null)], "each file keeps its path and its patch");
    }
}
