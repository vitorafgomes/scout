using System.Net;
using Scout.Plugin.GitHub.Abstractions;
using Scout.Plugin.GitHub.Tests.Fakes;

namespace Scout.Plugin.GitHub.Tests;

public sealed class GitHubClientPullRequestHeadTests : IDisposable
{
    private readonly StubHttpMessageHandler _handler = new();

    public void Dispose() => _handler.Dispose();

    private GitHubClient Client() => new(new HttpClient(_handler) { BaseAddress = new Uri("https://api.github.com/") });

    [Fact]
    public async Task The_Head_Of_A_Pull_Request_Is_Its_Head_Commit_And_Its_Author_In_One_Read()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, """{"number":42,"user":{"login":"alice"},"head":{"ref":"feat/x","sha":"0123456789abcdef"}}""");

        var head = await Client().GetPullRequestHeadAsync("octocat", "hello", 42, CancellationToken.None);

        head.ShouldBe(new PullRequestHead("0123456789abcdef", "alice"));
        _handler.LastRequest!.Method.ShouldBe(HttpMethod.Get);
        _handler.LastRequest.RequestUri!.ToString().ShouldBe("https://api.github.com/repos/octocat/hello/pulls/42");
    }

    [Fact]
    public async Task A_Pull_Request_That_Does_Not_Exist_Has_No_Head()
    {
        _handler.EnqueueJson(HttpStatusCode.NotFound, """{"message":"Not Found"}""");

        (await Client().GetPullRequestHeadAsync("octocat", "hello", 42, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task The_Signed_In_Login_Is_Read_From_The_User_Endpoint()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, """{"login":"octocat","id":1}""");

        (await Client().GetSignedInLoginAsync(CancellationToken.None)).ShouldBe("octocat");
        _handler.LastRequest!.RequestUri!.ToString().ShouldBe("https://api.github.com/user");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "GitHub denied the request (insufficient token permissions).")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "GitHub request failed (422).")]
    [InlineData(HttpStatusCode.BadGateway, "GitHub is currently unavailable. Try again later.")]
    public async Task A_Failed_Head_Or_Login_Read_Names_What_GitHub_Answered(HttpStatusCode status, string message)
    {
        _handler.EnqueueJson(status, "");
        _handler.EnqueueJson(status, "");

        (await Should.ThrowAsync<GitHubApiException>(() => Client().GetPullRequestHeadAsync("octocat", "hello", 42, CancellationToken.None))).Message.ShouldBe(message);
        (await Should.ThrowAsync<GitHubApiException>(() => Client().GetSignedInLoginAsync(CancellationToken.None))).Message.ShouldBe(message);
    }

    [Fact]
    public async Task A_Head_Or_Login_Read_That_Times_Out_Bubbles_For_The_Caller()
    {
        _handler.EnqueueThrow(new TaskCanceledException("timed out"));
        _handler.EnqueueThrow(new TaskCanceledException("timed out"));

        await Should.ThrowAsync<TaskCanceledException>(() => Client().GetPullRequestHeadAsync("octocat", "hello", 42, CancellationToken.None));
        await Should.ThrowAsync<TaskCanceledException>(() => Client().GetSignedInLoginAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_Reviews_Of_A_Pull_Request_Are_Read_With_Who_Gave_Them_The_Event_And_The_Commit()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, """[{"id":5,"user":{"login":"octocat"},"state":"APPROVED","commit_id":"abc1234","html_url":"https://github.com/octocat/hello/pull/42#pullrequestreview-5"}]""");

        var reviews = await Client().ListReviewsAsync("octocat", "hello", 42, CancellationToken.None);

        reviews.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(new PullRequestReview(5, "octocat", "APPROVED", "abc1234", "https://github.com/octocat/hello/pull/42#pullrequestreview-5"));
        _handler.LastRequest!.Method.ShouldBe(HttpMethod.Get);
        _handler.LastRequest.RequestUri!.ToString().ShouldBe("https://api.github.com/repos/octocat/hello/pulls/42/reviews?per_page=100&page=1");
    }

    [Fact]
    public async Task The_Reviews_Of_A_Pull_Request_That_Does_Not_Exist_Are_None_And_A_403_Says_So()
    {
        _handler.EnqueueJson(HttpStatusCode.NotFound, "");
        _handler.EnqueueJson(HttpStatusCode.Forbidden, "");

        (await Client().ListReviewsAsync("octocat", "hello", 42, CancellationToken.None)).ShouldBeNull();
        (await Should.ThrowAsync<GitHubApiException>(() => Client().ListReviewsAsync("octocat", "hello", 42, CancellationToken.None)))
            .Message.ShouldBe("GitHub denied the request (insufficient token permissions).");
    }

    [Fact]
    public async Task Reviews_Are_Read_Newest_First_From_The_Last_Page_Backwards()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, """[{"id":1,"state":"COMMENTED"},{"id":2,"state":"COMMENTED"}]""",
            r => r.Headers.Add("Link", "<https://api.github.com/repos/octocat/hello/pulls/42/reviews?per_page=100&page=2>; rel=\"next\", <https://api.github.com/repos/octocat/hello/pulls/42/reviews?per_page=100&page=2>; rel=\"last\""));
        _handler.EnqueueJson(HttpStatusCode.OK, """[{"id":3,"state":"APPROVED"},{"id":4,"state":"APPROVED"}]""");

        var reviews = await Client().ListReviewsAsync("octocat", "hello", 42, CancellationToken.None);

        reviews.ShouldNotBeNull().Select(r => r.Id).ShouldBe([4L, 3L, 2L, 1L]);
        _handler.LastRequest!.RequestUri!.ToString().ShouldBe("https://api.github.com/repos/octocat/hello/pulls/42/reviews?per_page=100&page=2");
    }

    [Fact]
    public async Task Reviews_Are_Read_From_At_Most_The_Last_Ten_Pages()
    {
        var requested = new List<string>();
        var handler = new PagedHandler(requested, last: 15);
        var client = new GitHubClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") });

        var reviews = await client.ListReviewsAsync("octocat", "hello", 42, CancellationToken.None);

        requested.ShouldBe(["page=1", "page=15", "page=14", "page=13", "page=12", "page=11", "page=10", "page=9", "page=8", "page=7", "page=6"]);
        reviews.ShouldNotBeNull().Select(r => r.Id).ShouldBe(Enumerable.Range(6, 10).Reverse().Select(p => (long)p).ToList());
        handler.Dispose();
    }

    [Fact]
    public async Task The_Latest_Conversation_Comments_Are_The_Last_Page_Newest_First()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, """[{"user":{"login":"a"},"body":"old","created_at":"2026-11-01T00:00:00Z"}]""",
            r => r.Headers.Add("Link", "<https://api.github.com/repos/octocat/hello/issues/42/comments?per_page=100&page=3>; rel=\"last\""));
        _handler.EnqueueJson(HttpStatusCode.OK, """[{"user":{"login":"b"},"body":"newer","created_at":"2026-11-06T00:00:00Z"},{"user":{"login":"c"},"body":"newest","created_at":"2026-11-07T00:00:00Z"}]""");

        var comments = await Client().ListLatestConversationCommentsAsync("octocat", "hello", 42, CancellationToken.None);

        comments.ShouldNotBeNull().Select(c => (c.AuthorLogin, c.Body)).ShouldBe([("c", "newest"), ("b", "newer")]);
        _handler.LastRequest!.RequestUri!.ToString().ShouldBe("https://api.github.com/repos/octocat/hello/issues/42/comments?per_page=100&page=3");
    }

    private sealed class PagedHandler(List<string> requested, int last) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var page = int.Parse(request.RequestUri!.Query.Split("page=")[^1], System.Globalization.CultureInfo.InvariantCulture);
            requested.Add($"page={page}");
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""[{"id":{{page}},"state":"COMMENTED"}]""") };
            response.Headers.Add("Link", $"<https://api.github.com/x?per_page=100&page={last}>; rel=\"last\"");
            return Task.FromResult(response);
        }
    }
}
