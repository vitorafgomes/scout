using System.Net;
using System.Text.Json;
using Scout.Plugin.GitHub.Tests.Fakes;

namespace Scout.Plugin.GitHub.Tests;

public sealed class GitHubPluginChecksTests : IDisposable
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private const string Head = $$$"""{"number":42,"user":{"login":"alice"},"head":{"ref":"feat/x","sha":"{{{Sha}}}"}}""";

    private readonly RecordingStub _handler = new();

    public void Dispose() => _handler.Dispose();

    private GitHubPlugin Plugin() => new(new GitHubClient(new HttpClient(_handler) { BaseAddress = new Uri("https://api.github.com/") }));

    [Fact]
    public async Task The_Checks_Are_The_Head_Commit_s_Check_Runs_Then_Its_Statuses_Folded_Into_The_Same_Shape()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, Head);
        _handler.EnqueueJson(HttpStatusCode.OK, """
            {"total_count":2,"check_runs":[
              {"name":"build","status":"completed","conclusion":"success","html_url":"https://github.com/octocat/hello/runs/1"},
              {"name":"tests","status":"in_progress","conclusion":null,"html_url":"https://github.com/octocat/hello/runs/2"}]}
            """);
        _handler.EnqueueJson(HttpStatusCode.OK, """
            {"state":"failure","statuses":[
              {"context":"ci/lint","state":"success","target_url":"https://ci.example/1"},
              {"context":"ci/e2e","state":"failure","target_url":"https://ci.example/2"},
              {"context":"ci/legacy","state":"error","target_url":"https://ci.example/3"},
              {"context":"ci/deploy","state":"pending","target_url":"https://ci.example/4"}]}
            """);

        var answer = await Plugin().GetPullRequestChecksAsync("octocat", "hello", 42, CancellationToken.None);

        answer.ShouldBe(
            $$"""{"sha":"{{Sha}}","checks":[""" +
            """{"name":"build","status":"completed","conclusion":"success","url":"https://github.com/octocat/hello/runs/1"},""" +
            """{"name":"tests","status":"in_progress","conclusion":null,"url":"https://github.com/octocat/hello/runs/2"},""" +
            """{"name":"ci/lint","status":"completed","conclusion":"success","url":"https://ci.example/1"},""" +
            """{"name":"ci/e2e","status":"completed","conclusion":"failure","url":"https://ci.example/2"},""" +
            """{"name":"ci/legacy","status":"completed","conclusion":"failure","url":"https://ci.example/3"},""" +
            """{"name":"ci/deploy","status":"in_progress","conclusion":null,"url":"https://ci.example/4"}]}""");
        _handler.Requests.ShouldBe([
            "GET https://api.github.com/repos/octocat/hello/pulls/42",
            $"GET https://api.github.com/repos/octocat/hello/commits/{Sha}/check-runs?per_page=100",
            $"GET https://api.github.com/repos/octocat/hello/commits/{Sha}/status?per_page=100",
        ]);
    }

    [Fact]
    public async Task A_Commit_With_More_Check_Runs_Than_One_Page_Says_How_Many_Are_Shown()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, Head);
        _handler.EnqueueJson(HttpStatusCode.OK, """{"total_count":140,"check_runs":[{"name":"a","status":"completed","conclusion":"success","html_url":"u"},{"name":"b","status":"completed","conclusion":"success","html_url":"u"}]}""");
        _handler.EnqueueJson(HttpStatusCode.OK, """{"statuses":[{"context":"ci","state":"success","target_url":"u"}]}""");

        var answer = JsonDocument.Parse(await Plugin().GetPullRequestChecksAsync("octocat", "hello", 42, CancellationToken.None)).RootElement;

        answer.GetProperty("note").GetString().ShouldBe("3 of 141 checks shown");
        answer.GetProperty("checks").GetArrayLength().ShouldBe(3);
    }

    [Fact]
    public async Task A_Commit_Whose_Checks_Fit_One_Page_Answers_No_Note()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, Head);
        _handler.EnqueueJson(HttpStatusCode.OK, """{"total_count":1,"check_runs":[{"name":"a","status":"completed","conclusion":"success","html_url":"u"}]}""");
        _handler.EnqueueJson(HttpStatusCode.OK, """{"statuses":[]}""");

        var answer = JsonDocument.Parse(await Plugin().GetPullRequestChecksAsync("octocat", "hello", 42, CancellationToken.None)).RootElement;

        answer.TryGetProperty("note", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_Commit_With_No_Checks_Answers_An_Empty_List()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, Head);
        _handler.EnqueueJson(HttpStatusCode.OK, """{"total_count":0,"check_runs":[]}""");
        _handler.EnqueueJson(HttpStatusCode.OK, """{"state":"pending","statuses":[]}""");

        var answer = JsonDocument.Parse(await Plugin().GetPullRequestChecksAsync("octocat", "hello", 42, CancellationToken.None)).RootElement;

        answer.GetProperty("checks").GetArrayLength().ShouldBe(0);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "GitHub denied the request (insufficient token permissions).")]
    [InlineData(HttpStatusCode.NotFound, "Repository not found or the token has no access.")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "GitHub request failed (422).")]
    public async Task A_Failed_Read_Of_The_Checks_Is_A_Sentence_Not_A_Fault(HttpStatusCode status, string sentence)
    {
        _handler.EnqueueJson(HttpStatusCode.OK, Head);
        _handler.EnqueueJson(status, "");
        _handler.EnqueueJson(HttpStatusCode.OK, """{"statuses":[]}""");

        (await Plugin().GetPullRequestChecksAsync("octocat", "hello", 42, CancellationToken.None)).ShouldBe(sentence);
    }

    [Fact]
    public async Task A_Status_Read_That_Fails_After_The_Check_Runs_Were_Read_Is_A_Sentence()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, Head);
        _handler.EnqueueJson(HttpStatusCode.OK, """{"total_count":0,"check_runs":[]}""");
        _handler.EnqueueJson(HttpStatusCode.ServiceUnavailable, "");

        (await Plugin().GetPullRequestChecksAsync("octocat", "hello", 42, CancellationToken.None)).ShouldBe("GitHub is currently unavailable. Try again later.");
        _handler.Requests.Last().ShouldBe($"GET https://api.github.com/repos/octocat/hello/commits/{Sha}/status?per_page=100");
    }

    [Fact]
    public async Task A_Status_Read_That_Times_Out_After_The_Check_Runs_Were_Read_Is_A_Sentence()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, Head);
        _handler.EnqueueJson(HttpStatusCode.OK, """{"total_count":0,"check_runs":[]}""");
        _handler.EnqueueThrow(new TaskCanceledException("timed out"));

        (await Plugin().GetPullRequestChecksAsync("octocat", "hello", 42, CancellationToken.None)).ShouldBe("GitHub request timed out. Try again later.");
        _handler.Requests.Last().ShouldBe($"GET https://api.github.com/repos/octocat/hello/commits/{Sha}/status?per_page=100");
    }

    [Fact]
    public async Task A_Read_Of_The_Checks_That_Times_Out_Is_A_Sentence()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, Head);
        _handler.EnqueueThrow(new TaskCanceledException("timed out"));
        _handler.EnqueueJson(HttpStatusCode.OK, """{"statuses":[]}""");

        (await Plugin().GetPullRequestChecksAsync("octocat", "hello", 42, CancellationToken.None)).ShouldBe("GitHub request timed out. Try again later.");
    }

    [Fact]
    public async Task A_Pull_Request_That_Does_Not_Exist_Or_A_Bad_Name_Reads_No_Checks()
    {
        _handler.EnqueueJson(HttpStatusCode.NotFound, "");

        (await Plugin().GetPullRequestChecksAsync("octocat", "hello", 42, CancellationToken.None)).ShouldBe("Pull request #42 not found in octocat/hello or not accessible.");
        (await Plugin().GetPullRequestChecksAsync("octo cat", "hello", 42, CancellationToken.None)).ShouldBe(GitHubNames.InvalidRepository);
        _handler.Requests.ShouldHaveSingleItem();
    }

    private sealed class RecordingStub : HttpMessageHandler
    {
        private readonly StubHttpMessageHandler _inner = new();
        private readonly HttpMessageInvoker _invoker;

        public RecordingStub() => _invoker = new HttpMessageInvoker(_inner);

        public List<string> Requests { get; } = [];

        public void EnqueueJson(HttpStatusCode status, string json) => _inner.EnqueueJson(status, json);

        public void EnqueueThrow(Exception exception) => _inner.EnqueueThrow(exception);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method.Method} {request.RequestUri}");
            return _invoker.SendAsync(request, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _invoker.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
