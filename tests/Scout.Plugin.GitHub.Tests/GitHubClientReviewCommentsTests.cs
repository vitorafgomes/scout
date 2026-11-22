using System.Net;
using Scout.Plugin.GitHub.Abstractions;
using Scout.Plugin.GitHub.Tests.Fakes;

namespace Scout.Plugin.GitHub.Tests;

/// <summary>The read of a pull request's review comments with what links each to its conversation and its commit.</summary>
public sealed class GitHubClientReviewCommentsTests
{
    private static (GitHubClient Client, StubHttpMessageHandler Handler) Build()
    {
        var handler = new StubHttpMessageHandler();
        return (new GitHubClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") }), handler);
    }

    [Fact]
    public async Task E1_the_review_comments_come_with_their_conversation_commit_and_address_oldest_first()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, """
            [{"id":11,"in_reply_to_id":7,"user":{"login":"alice"},"body":"It is intended.","path":"src/A.cs","line":null,"original_line":4,
              "commit_id":"bbb","original_commit_id":"aaa","created_at":"2026-11-20T10:05:00Z","html_url":"https://github.com/o/r/pull/5#discussion_r11"},
             {"id":7,"user":{"login":"scout-bot[bot]"},"body":"B","path":"src/A.cs","line":9,"original_line":4,
              "commit_id":"bbb","original_commit_id":"aaa","created_at":"2026-11-20T10:00:00Z","html_url":"https://github.com/o/r/pull/5#discussion_r7"}]
            """);

        var read = await client.ListReviewCommentsAsync("o", "r", 5, CancellationToken.None);

        handler.LastRequest!.Method.ShouldBe(HttpMethod.Get, "the comments are read with a GET, as every read of the plugin");
        handler.LastRequest.RequestUri!.PathAndQuery.ShouldBe("/repos/o/r/pulls/5/comments?per_page=100", "one page of 100 is read");
        read.ShouldNotBeNull().Comments.ShouldBe(
            [
                new PullRequestReviewComment(7, null, "scout-bot[bot]", "B", "src/A.cs", 9, 4, "bbb", "aaa", DateTimeOffset.Parse("2026-11-20T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture), "https://github.com/o/r/pull/5#discussion_r7"),
                new PullRequestReviewComment(11, 7, "alice", "It is intended.", "src/A.cs", null, 4, "bbb", "aaa", DateTimeOffset.Parse("2026-11-20T10:05:00Z", System.Globalization.CultureInfo.InvariantCulture), "https://github.com/o/r/pull/5#discussion_r11"),
            ],
            "each comment keeps its id, the comment it answers, its author, text, file, lines, commits and address, oldest first");
        read.More.ShouldBeFalse("a page that is not full is every comment");
    }

    [Fact]
    public async Task E1_a_full_page_says_there_are_more_comments_than_were_read_and_a_missing_pull_request_is_none()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, "[" + string.Join(",", Enumerable.Range(1, 100).Select(i => $$"""{"id":{{i}},"body":"b","created_at":"2026-11-20T10:00:00Z"}""")) + "]");
        handler.EnqueueJson(HttpStatusCode.NotFound, """{"message":"Not Found"}""");

        var full = await client.ListReviewCommentsAsync("o", "r", 5, CancellationToken.None);
        var missing = await client.ListReviewCommentsAsync("o", "r", 6, CancellationToken.None);

        (full.ShouldNotBeNull().Comments.Count, full.More).ShouldBe((100, true), "a full page of 100 says there may be more");
        missing.ShouldBeNull("a pull request GitHub does not find has no comments to read");
    }

    [Fact]
    public async Task E5_a_read_that_fails_with_a_server_error_throws_and_a_timeout_bubbles()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.BadGateway, """{"message":"Server Error"}""");
        handler.EnqueueThrow(new TaskCanceledException("timed out"));

        await Should.ThrowAsync<GitHubApiException>(() => client.ListReviewCommentsAsync("o", "r", 5, CancellationToken.None), "a 5xx is a failed read, never no comments");
        await Should.ThrowAsync<TaskCanceledException>(() => client.ListReviewCommentsAsync("o", "r", 5, CancellationToken.None), "a timeout reaches the caller, which says the earlier findings were not checked");
    }
}
