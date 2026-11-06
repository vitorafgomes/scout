using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Plugin.GitHub.Abstractions;

namespace Scout.Plugin.GitHub.Tests;

public sealed class GitHubTokenFileHandlerTests : IDisposable
{
    private const string NotConnected = "GitHub is not connected. Connect it on the Features screen.";

    private readonly string _dir = Directory.CreateTempSubdirectory("scout-github-token-").FullName;
    private readonly List<RecordedRequest> _requests = [];
    private Func<HttpRequestMessage, HttpResponseMessage> _respond =
        _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };

    private string TokenFile => Path.Combine(_dir, "github-token.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ServiceProvider Compose(string key = "TokenFile", string? value = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Plugins:GitHub:" + key] = value ?? TokenFile,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddGitHubPlugin(config);
        services.AddHttpClient(nameof(IGitHubClient))
            .ConfigurePrimaryHttpMessageHandler(() => new RecordingHandler(_requests, r => _respond(r)));
        services.AddHttpClient(nameof(IGitHubWriteClient))
            .ConfigurePrimaryHttpMessageHandler(() => new RecordingHandler(_requests, r => _respond(r)));
        return services.BuildServiceProvider();
    }

    private void WriteToken(string json, DateTime writtenAtUtc)
    {
        File.WriteAllText(TokenFile, json);
        File.SetLastWriteTimeUtc(TokenFile, writtenAtUtc);
    }

    [Fact]
    public async Task The_Bot_Uses_The_Token_From_The_File_And_The_Rewritten_Token_After_A_Refresh()
    {
        WriteToken("""{"access_token":"ghu_first","expires_at":"2026-10-23T20:00:00Z"}""", new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc));
        using var provider = Compose();
        var client = provider.GetRequiredService<IGitHubClient>();

        await client.ListRepositoriesAsync(CancellationToken.None);
        WriteToken("""{"access_token":"ghu_second","expires_at":"2026-10-24T04:00:00Z"}""", new DateTime(2026, 10, 23, 19, 56, 0, DateTimeKind.Utc));
        await client.ListRepositoriesAsync(CancellationToken.None);

        _requests.Select(r => r.Authorization).ShouldBe(["Bearer ghu_first", "Bearer ghu_second"]);
    }

    [Fact]
    public void A_Configured_Token_File_Registers_The_Plugin_Before_The_Owner_Connects()
    {
        using var provider = Compose();

        provider.GetServices<KernelPlugin>().Select(p => p.Name).ShouldBe(["GitHub", "GitHubActions"]);
    }

    [Fact]
    public async Task A_Missing_Token_File_Answers_Not_Connected_And_Calls_No_One()
    {
        using var provider = Compose();
        var plugin = provider.GetServices<KernelPlugin>().Single(p => p.Name == "GitHub");

        var result = await plugin["list_repositories"].InvokeAsync(new Kernel());

        result.GetValue<string>().ShouldBe(NotConnected);
        _requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("""{"access_token":"  "}""")]
    public async Task An_Empty_Or_Unreadable_Token_File_Fails_As_Not_Connected_Without_A_Call(string contents)
    {
        WriteToken(contents, new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc));
        using var provider = Compose();

        var ex = await Should.ThrowAsync<GitHubApiException>(
            () => provider.GetRequiredService<IGitHubClient>().ListRepositoriesAsync(CancellationToken.None));

        ex.Message.ShouldBe(NotConnected);
        _requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Token_File_Deleted_On_Sign_Out_Stops_The_Old_Token_Being_Sent()
    {
        WriteToken("""{"access_token":"ghu_signed_out"}""", new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc));
        using var provider = Compose();
        var client = provider.GetRequiredService<IGitHubClient>();
        await client.ListRepositoriesAsync(CancellationToken.None);

        File.Delete(TokenFile);
        var ex = await Should.ThrowAsync<GitHubApiException>(
            () => client.ListRepositoriesAsync(CancellationToken.None));

        ex.Message.ShouldBe(NotConnected);
        _requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_401_From_GitHub_Asks_The_Owner_To_Connect_Again_Without_The_Token()
    {
        WriteToken("""{"access_token":"ghu_expired_value"}""", new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc));
        _respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") };
        using var provider = Compose();

        var ex = await Should.ThrowAsync<GitHubApiException>(
            () => provider.GetRequiredService<IGitHubClient>().ListRepositoriesAsync(CancellationToken.None));

        ex.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        ex.Message.ShouldBe(
            "GitHub rejected the connection. Disconnect and connect GitHub again on the Features screen.");
        ex.Message.ShouldNotContain("ghu_expired_value");
    }

    [Fact]
    public async Task A_401_For_A_Configured_Token_Keeps_The_Check_The_Token_Message()
    {
        _respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") };
        using var provider = Compose("Token", "ghp_static_value");

        var ex = await Should.ThrowAsync<GitHubApiException>(
            () => provider.GetRequiredService<IGitHubClient>().ListRepositoriesAsync(CancellationToken.None));

        ex.Message.ShouldBe("GitHub authentication failed. Check the configured token.");
        ex.Message.ShouldNotContain("ghp_static_value");
    }

    [Fact]
    public async Task Every_Request_Any_GitHub_Function_Sends_To_The_GitHub_Api_Is_A_Get()
    {
        WriteToken("""{"access_token":"ghu_read_only"}""", new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc));
        _respond = request => request.RequestUri!.AbsolutePath switch
        {
            "/repos/octocat/hello-world" => Json("""{"full_name":"octocat/hello-world","private":false}"""),
            "/repos/octocat/hello-world/pulls/1" => Json("""{"number":1,"title":"t","user":{"login":"a"},"base":{"ref":"main"},"head":{"ref":"f","sha":"abc"}}"""),
            "/repos/octocat/hello-world/issues/1" => Json("""{"number":1,"title":"t","user":{"login":"a"},"labels":[]}"""),
            "/repos/octocat/hello-world/contents/README.md" => Json("""{"type":"file","encoding":"base64","size":0,"content":""}"""),
            "/repos/octocat/hello-world/git/trees/main" => Json("""{"truncated":false,"tree":[]}"""),
            "/search/code" => Json("""{"total_count":0,"items":[]}"""),
            "/user/repos" or "/repos/octocat/hello-world/pulls" or "/repos/octocat/hello-world/issues"
                or "/repos/octocat/hello-world/pulls/1/files" or "/repos/octocat/hello-world/pulls/1/comments"
                or "/repos/octocat/hello-world/issues/1/comments" => Json("[]"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        using var provider = Compose();
        var plugin = provider.GetServices<KernelPlugin>().Single(p => p.Name == "GitHub");
        var arguments = new KernelArguments
        {
            ["owner"] = "octocat",
            ["repo"] = "hello-world",
            ["number"] = 1,
            ["path"] = "README.md",
            ["reference"] = "main",
            ["prefix"] = "",
            ["extensions"] = "",
            ["query"] = "token",
            ["extension"] = "",
        };

        foreach (var function in plugin)
        {
            await function.InvokeAsync(new Kernel(), arguments);
        }

        var paths = _requests.Select(r => r.Path).ToList();
        paths.ShouldContain("/user/repos");
        paths.ShouldContain("/repos/octocat/hello-world/pulls");
        paths.ShouldContain("/repos/octocat/hello-world/issues");
        paths.ShouldContain("/repos/octocat/hello-world");
        paths.ShouldContain("/repos/octocat/hello-world/pulls/1");
        paths.ShouldContain("/repos/octocat/hello-world/pulls/1/files");
        paths.ShouldContain("/repos/octocat/hello-world/pulls/1/comments");
        paths.ShouldContain("/repos/octocat/hello-world/issues/1/comments");
        paths.ShouldContain("/repos/octocat/hello-world/issues/1");
        paths.ShouldContain("/repos/octocat/hello-world/contents/README.md");
        paths.ShouldContain("/repos/octocat/hello-world/git/trees/main");
        paths.ShouldContain("/search/code");
        _requests.ShouldAllBe(r => r.Method == "GET" && r.Host == "api.github.com");
    }

    [Fact]
    public async Task The_Only_Requests_That_Are_Not_A_Get_Come_From_The_Write_Client_To_Its_Two_Endpoints_With_The_Token_From_The_File()
    {
        WriteToken("""{"access_token":"ghu_write"}""", new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc));
        _respond = request => request.RequestUri!.AbsolutePath switch
        {
            "/repos/octocat/hello-world/pulls/1/reviews" => Json("""{"id":1,"html_url":"u"}"""),
            "/repos/octocat/hello-world/issues" => Json("""{"number":2,"html_url":"u"}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        using var provider = Compose();
        var writes = provider.GetRequiredService<IGitHubWriteClient>();

        await writes.SubmitReviewAsync("octocat", "hello-world", 1, "b", [], CancellationToken.None);
        await writes.CreateIssueAsync("octocat", "hello-world", "t", "b", [], CancellationToken.None);

        _requests.Select(r => (r.Method, r.Path, r.Authorization)).ShouldBe([
            ("POST", "/repos/octocat/hello-world/pulls/1/reviews", "Bearer ghu_write"),
            ("POST", "/repos/octocat/hello-world/issues", "Bearer ghu_write"),
        ]);
    }

    [Fact]
    public async Task The_Two_Write_Functions_Post_Only_To_Their_Endpoints_With_The_Token_From_The_File_And_Answer_With_The_Url()
    {
        WriteToken("""{"access_token":"ghu_write"}""", new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc));
        _respond = request => request.RequestUri!.AbsolutePath switch
        {
            "/repos/octocat/hello-world/pulls/1/files" => Json("""[{"filename":"a.cs","status":"modified","additions":1,"deletions":0,"patch":"@@ -1 +1 @@\n-x\n+y"}]"""),
            "/repos/octocat/hello-world/pulls/1/reviews" => Json("""{"id":1,"html_url":"https://github.com/octocat/hello-world/pull/1#pullrequestreview-1"}"""),
            "/repos/octocat/hello-world/issues" => Json("""{"number":2,"html_url":"https://github.com/octocat/hello-world/issues/2"}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        using var provider = Compose();
        var actions = provider.GetServices<KernelPlugin>().Single(p => p.Name == "GitHubActions");
        var review = new KernelArguments
        {
            ["owner"] = "octocat",
            ["repo"] = "hello-world",
            ["number"] = 1,
            ["body"] = "b",
            ["comments"] = """[{"path":"a.cs","line":1,"body":"c"}]""",
        };
        var issue = new KernelArguments
        {
            ["owner"] = "octocat",
            ["repo"] = "hello-world",
            ["title"] = "t",
            ["body"] = "b",
            ["labels"] = "",
        };

        var reviewAnswer = (await actions["submit_pull_request_review"].InvokeAsync(new Kernel(), review)).ToString();
        var issueAnswer = (await actions["create_issue"].InvokeAsync(new Kernel(), issue)).ToString();

        reviewAnswer.ShouldBe("Posted a review on octocat/hello-world#1 with 1 comment(s): https://github.com/octocat/hello-world/pull/1#pullrequestreview-1");
        issueAnswer.ShouldBe("Opened issue #2 on octocat/hello-world: https://github.com/octocat/hello-world/issues/2");
        _requests.Select(r => (r.Method, r.Path, r.Authorization)).ShouldBe([
            ("GET", "/repos/octocat/hello-world/pulls/1/files", "Bearer ghu_write"),
            ("POST", "/repos/octocat/hello-world/pulls/1/reviews", "Bearer ghu_write"),
            ("POST", "/repos/octocat/hello-world/issues", "Bearer ghu_write"),
        ]);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed record RecordedRequest(string Method, string Host, string Path, string? Authorization);

    private sealed class RecordingHandler(
        List<RecordedRequest> requests,
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Add(new RecordedRequest(
                request.Method.Method,
                request.RequestUri!.Host,
                request.RequestUri.AbsolutePath,
                request.Headers.Authorization?.ToString()));
            return Task.FromResult(respond(request));
        }
    }
}
