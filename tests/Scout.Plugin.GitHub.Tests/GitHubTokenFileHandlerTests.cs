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

        provider.GetServices<KernelPlugin>().ShouldHaveSingleItem().Name.ShouldBe("GitHub");
    }

    [Fact]
    public async Task A_Missing_Token_File_Answers_Not_Connected_And_Calls_No_One()
    {
        using var provider = Compose();
        var plugin = provider.GetServices<KernelPlugin>().Single();

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
            "/repos/octocat/hello-world" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"full_name":"octocat/hello-world","private":false}"""),
            },
            "/user/repos" or "/repos/octocat/hello-world/pulls" or "/repos/octocat/hello-world/issues"
                => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        using var provider = Compose();
        var plugin = provider.GetServices<KernelPlugin>().Single();
        var repo = new KernelArguments { ["owner"] = "octocat", ["repo"] = "hello-world" };

        foreach (var function in plugin)
        {
            await function.InvokeAsync(new Kernel(), repo);
        }

        _requests.Select(r => r.Path).ShouldContain("/user/repos");
        _requests.Select(r => r.Path).ShouldContain("/repos/octocat/hello-world/pulls");
        _requests.Select(r => r.Path).ShouldContain("/repos/octocat/hello-world/issues");
        _requests.Select(r => r.Path).ShouldContain("/repos/octocat/hello-world");
        _requests.ShouldAllBe(r => r.Method == "GET" && r.Host == "api.github.com");
    }

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
