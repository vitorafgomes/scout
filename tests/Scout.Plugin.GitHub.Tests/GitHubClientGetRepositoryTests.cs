using System.Net;
using System.Text;
using Scout.Plugin.GitHub.Tests.Fakes;

namespace Scout.Plugin.GitHub.Tests;

/// <summary>
/// Covers <see cref="GitHubClient.GetRepositoryAsync"/>: field mapping, README
/// base64 decoding, top-level structure, and resilience at the external HTTP
/// boundary (404 for the repo returns null; optional sub-calls failing leave
/// their part empty rather than failing the whole call).
/// </summary>
public sealed class GitHubClientGetRepositoryTests
{
    private static readonly string[] ExpectedTopics = ["dotnet", "messaging", "cli"];

    private static readonly string[] ExpectedEntries = ["src (dir)", "tests (dir)", ".github (dir)", "README.md (file)"];

    private static (GitHubClient client, StubHttpMessageHandler handler) Build()
    {
        var handler = new StubHttpMessageHandler();
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.github.com/"),
        };
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-token");
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return (new GitHubClient(http), handler);
    }

    private static string Base64(string text)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private const string RepoJson = """
        {
          "full_name": "example-owner/sample-app",
          "description": "A .NET messaging exploration tool",
          "language": "C#",
          "topics": ["dotnet", "messaging", "cli"],
          "license": { "spdx_id": "MIT" },
          "default_branch": "main",
          "stargazers_count": 5,
          "forks_count": 2,
          "html_url": "https://github.com/example-owner/sample-app"
        }
        """;

    private const string LanguagesJson = """{ "C#": 12000, "HTML": 800 }""";

    private static string ReadmeJson => $$"""
        { "content": "{{Base64("# sample-app\nExplore messaging.")}}", "encoding": "base64" }
        """;

    private const string ContentsJson = """
        [
          { "name": "src", "type": "dir" },
          { "name": "tests", "type": "dir" },
          { "name": ".github", "type": "dir" },
          { "name": "README.md", "type": "file" }
        ]
        """;

    private static void EnqueueAllOk(StubHttpMessageHandler handler)
    {
        handler.EnqueueJson(HttpStatusCode.OK, RepoJson);
        handler.EnqueueJson(HttpStatusCode.OK, LanguagesJson);
        handler.EnqueueJson(HttpStatusCode.OK, ReadmeJson);
        handler.EnqueueJson(HttpStatusCode.OK, ContentsJson);
    }

    [Fact]
    public async Task GetRepository_Maps_Core_Metadata()
    {
        var (client, handler) = Build();
        EnqueueAllOk(handler);

        var info = await client.GetRepositoryAsync(
            "example-owner", "sample-app", CancellationToken.None);

        info.ShouldNotBeNull();
        info!.FullName.ShouldBe("example-owner/sample-app");
        info.Description.ShouldBe("A .NET messaging exploration tool");
        info.Language.ShouldBe("C#");
        info.License.ShouldBe("MIT");
        info.DefaultBranch.ShouldBe("main");
        info.StarCount.ShouldBe(5);
        info.ForkCount.ShouldBe(2);
        info.HtmlUrl.ShouldBe("https://github.com/example-owner/sample-app");
        info.Topics.ShouldBe(ExpectedTopics);
    }

    [Fact]
    public async Task GetRepository_Maps_Language_Breakdown_Descending()
    {
        var (client, handler) = Build();
        EnqueueAllOk(handler);

        var info = await client.GetRepositoryAsync(
            "example-owner", "sample-app", CancellationToken.None);

        info!.Languages.Count.ShouldBe(2);
        info.Languages[0].Name.ShouldBe("C#");
        info.Languages[0].Bytes.ShouldBe(12000);
        info.Languages[1].Name.ShouldBe("HTML");
        info.Languages[1].Bytes.ShouldBe(800);
    }

    [Fact]
    public async Task GetRepository_Decodes_Readme_From_Base64()
    {
        var (client, handler) = Build();
        EnqueueAllOk(handler);

        var info = await client.GetRepositoryAsync(
            "example-owner", "sample-app", CancellationToken.None);

        info!.Readme.ShouldNotBeNull();
        info.Readme!.ShouldContain("# sample-app");
        info.Readme.ShouldContain("Explore messaging.");
    }

    [Fact]
    public async Task GetRepository_Lists_TopLevel_Entries()
    {
        var (client, handler) = Build();
        EnqueueAllOk(handler);

        var info = await client.GetRepositoryAsync(
            "example-owner", "sample-app", CancellationToken.None);

        info!.TopLevelEntries.ShouldBe(ExpectedEntries);
    }

    [Fact]
    public async Task GetRepository_Requests_Repo_Endpoint_First()
    {
        var (client, handler) = Build();
        EnqueueAllOk(handler);

        await client.GetRepositoryAsync(
            "example-owner", "sample-app", CancellationToken.None);

        // Last request is the contents call; assert the path shape.
        handler.LastRequest!.RequestUri!.ToString()
            .ShouldContain("repos/example-owner/sample-app/contents");
    }

    [Fact]
    public async Task GetRepository_NotFound_Returns_Null()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.NotFound, "{}");

        var info = await client.GetRepositoryAsync(
            "example-owner", "ghost", CancellationToken.None);

        info.ShouldBeNull();
    }

    [Fact]
    public async Task GetRepository_ServerError_Throws_Unavailable_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.InternalServerError, "{}");

        var ex = await Should.ThrowAsync<GitHubApiException>(
            () => client.GetRepositoryAsync("example-owner", "sample-app", CancellationToken.None));

        ex.Message.ShouldContain("unavailable");
    }

    [Fact]
    public async Task GetRepository_Transport_Failure_Propagates_As_HttpRequestException()
    {
        var (client, handler) = Build();
        handler.EnqueueThrow(new HttpRequestException("connection reset"));

        await Should.ThrowAsync<HttpRequestException>(
            () => client.GetRepositoryAsync("example-owner", "sample-app", CancellationToken.None));
    }

    [Fact]
    public async Task GetRepository_Readme_NotFound_Returns_Info_With_Null_Readme()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, RepoJson);
        handler.EnqueueJson(HttpStatusCode.OK, LanguagesJson);
        handler.EnqueueJson(HttpStatusCode.NotFound, "{}"); // README missing
        handler.EnqueueJson(HttpStatusCode.OK, ContentsJson);

        var info = await client.GetRepositoryAsync(
            "example-owner", "sample-app", CancellationToken.None);

        info.ShouldNotBeNull();
        info!.Readme.ShouldBeNull();
        info.TopLevelEntries.Count.ShouldBe(4);
        info.Language.ShouldBe("C#");
    }

    [Fact]
    public async Task GetRepository_Contents_NotFound_Returns_Info_With_Empty_Entries()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, RepoJson);
        handler.EnqueueJson(HttpStatusCode.OK, LanguagesJson);
        handler.EnqueueJson(HttpStatusCode.OK, ReadmeJson);
        handler.EnqueueJson(HttpStatusCode.NotFound, "{}"); // contents missing

        var info = await client.GetRepositoryAsync(
            "example-owner", "sample-app", CancellationToken.None);

        info.ShouldNotBeNull();
        info!.TopLevelEntries.ShouldBeEmpty();
        info.Readme.ShouldNotBeNull();
    }

    [Fact]
    public async Task GetRepository_Truncates_Long_Readme()
    {
        var (client, handler) = Build();
        var longText = new string('x', 5000);
        handler.EnqueueJson(HttpStatusCode.OK, RepoJson);
        handler.EnqueueJson(HttpStatusCode.OK, LanguagesJson);
        handler.EnqueueJson(HttpStatusCode.OK,
            $$"""{ "content": "{{Base64(longText)}}", "encoding": "base64" }""");
        handler.EnqueueJson(HttpStatusCode.OK, ContentsJson);

        var info = await client.GetRepositoryAsync(
            "example-owner", "sample-app", CancellationToken.None);

        info!.Readme!.Length.ShouldBeLessThanOrEqualTo(2100);
    }
}
