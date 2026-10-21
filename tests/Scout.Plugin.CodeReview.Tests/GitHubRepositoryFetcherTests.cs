using System.Net;
using System.Text;
using Scout.Plugin.CodeReview.Abstractions;
using Scout.Plugin.CodeReview.Tests.Fakes;

namespace Scout.Plugin.CodeReview.Tests;

public sealed class GitHubRepositoryFetcherTests
{
    private const int MaxFileBytes = 200_000;
    private const int MaxFiles = 12;

    private static (GitHubRepositoryFetcher fetcher, StubHttpMessageHandler handler) Build(
        int maxFileBytes = MaxFileBytes, int maxFiles = MaxFiles)
    {
        var handler = new StubHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        return (new GitHubRepositoryFetcher(http, maxFileBytes, maxFiles), handler);
    }

    private static string Base64(string text)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static string FileJson(string base64)
        => $$"""{"content":"{{base64}}","encoding":"base64"}""";

    private static readonly RepositoryCoordinates Coords = new("octocat", "hello-world");

    [Fact]
    public async Task Resolves_Default_Branch_Not_Hardcoded_Main()
    {
        var (fetcher, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"full_name":"octocat/hello-world","default_branch":"develop"}""");
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"truncated":false,"tree":[{"path":"Program.cs","type":"blob","size":10,"sha":"s"}]}""");
        handler.EnqueueJson(HttpStatusCode.OK, FileJson(Base64("class P {}")));

        var snapshot = await fetcher.FetchAsync(Coords, CancellationToken.None);

        snapshot.Branch.ShouldBe("develop");
        handler.RequestedPaths.ShouldContain(p => p.Contains("git/trees/develop"));
        handler.RequestedPaths.ShouldContain(p => p.Contains("ref=develop"));
    }

    [Fact]
    public async Task Calls_Repo_Then_Tree_Then_Contents_In_Order()
    {
        var (fetcher, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"full_name":"octocat/hello-world","default_branch":"main"}""");
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"truncated":false,"tree":[{"path":"Program.cs","type":"blob","size":10,"sha":"s"}]}""");
        handler.EnqueueJson(HttpStatusCode.OK, FileJson(Base64("class P {}")));

        var snapshot = await fetcher.FetchAsync(Coords, CancellationToken.None);

        handler.RequestedPaths[0].ShouldContain("repos/octocat/hello-world");
        handler.RequestedPaths[1].ShouldContain("git/trees/main");
        handler.RequestedPaths[2].ShouldContain("contents/Program.cs");
        snapshot.Files.ShouldHaveSingleItem().Path.ShouldBe("Program.cs");
        snapshot.Files[0].Content.ShouldBe("class P {}");
    }

    [Fact]
    public async Task Truncated_Tree_Throws_Fetch_Exception()
    {
        var (fetcher, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"full_name":"octocat/hello-world","default_branch":"main"}""");
        handler.EnqueueJson(HttpStatusCode.OK, """{"truncated":true,"tree":[]}""");

        await Should.ThrowAsync<CodeReviewFetchException>(
            () => fetcher.FetchAsync(Coords, CancellationToken.None));
    }

    [Fact]
    public async Task Skips_Oversize_File()
    {
        var (fetcher, handler) = Build(maxFileBytes: 4);
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"full_name":"octocat/hello-world","default_branch":"main"}""");
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"truncated":false,"tree":[{"path":"Program.cs","type":"blob","size":100,"sha":"s"}]}""");
        handler.EnqueueJson(HttpStatusCode.OK, FileJson(Base64("this is more than four bytes")));

        var snapshot = await fetcher.FetchAsync(Coords, CancellationToken.None);

        snapshot.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task Skips_Binary_File_With_Nul_Byte()
    {
        var (fetcher, handler) = Build();
        var binary = Convert.ToBase64String(new byte[] { 0x41, 0x00, 0x42 });
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"full_name":"octocat/hello-world","default_branch":"main"}""");
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"truncated":false,"tree":[{"path":"data.json","type":"blob","size":3,"sha":"s"}]}""");
        handler.EnqueueJson(HttpStatusCode.OK, $$"""{"content":"{{binary}}","encoding":"base64"}""");

        var snapshot = await fetcher.FetchAsync(Coords, CancellationToken.None);

        snapshot.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task Skips_Invalid_Utf8_File()
    {
        var (fetcher, handler) = Build();
        // 0xC3 0x28 is an invalid UTF-8 sequence (no NUL byte involved).
        var invalid = Convert.ToBase64String(new byte[] { 0xC3, 0x28 });
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"full_name":"octocat/hello-world","default_branch":"main"}""");
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"truncated":false,"tree":[{"path":"weird.txt","type":"blob","size":2,"sha":"s"}]}""");
        handler.EnqueueJson(HttpStatusCode.OK, $$"""{"content":"{{invalid}}","encoding":"base64"}""");

        var snapshot = await fetcher.FetchAsync(Coords, CancellationToken.None);

        snapshot.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task Skips_Individually_Missing_File()
    {
        var (fetcher, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"full_name":"octocat/hello-world","default_branch":"main"}""");
        // Two plain-source files of equal rank/depth/size order by path: "Gone.cs"
        // is fetched first (404, skipped), then "Keep.cs" succeeds.
        handler.EnqueueJson(HttpStatusCode.OK,
            """
            {"truncated":false,"tree":[
              {"path":"Gone.cs","type":"blob","size":10,"sha":"s"},
              {"path":"Keep.cs","type":"blob","size":10,"sha":"s"}
            ]}
            """);
        handler.EnqueueJson(HttpStatusCode.NotFound, "{}");
        handler.EnqueueJson(HttpStatusCode.OK, FileJson(Base64("ok")));

        var snapshot = await fetcher.FetchAsync(Coords, CancellationToken.None);

        snapshot.Files.ShouldHaveSingleItem().Path.ShouldBe("Keep.cs");
    }

    [Fact]
    public async Task Skips_Single_File_Server_Error_And_Keeps_Rest()
    {
        var (fetcher, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"full_name":"octocat/hello-world","default_branch":"main"}""");
        // Three plain-source files; the middle one (by path order) fails with 500.
        handler.EnqueueJson(HttpStatusCode.OK,
            """
            {"truncated":false,"tree":[
              {"path":"Alpha.cs","type":"blob","size":10,"sha":"s"},
              {"path":"Bravo.cs","type":"blob","size":10,"sha":"s"},
              {"path":"Charlie.cs","type":"blob","size":10,"sha":"s"}
            ]}
            """);
        handler.EnqueueJson(HttpStatusCode.OK, FileJson(Base64("alpha")));
        handler.EnqueueJson(HttpStatusCode.InternalServerError, "{}");
        handler.EnqueueJson(HttpStatusCode.OK, FileJson(Base64("charlie")));

        var snapshot = await fetcher.FetchAsync(Coords, CancellationToken.None);

        snapshot.Files.Select(f => f.Path).ShouldBe(new[] { "Alpha.cs", "Charlie.cs" });
    }

    [Fact]
    public async Task Skips_Single_File_Forbidden_And_Keeps_Rest()
    {
        var (fetcher, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"full_name":"octocat/hello-world","default_branch":"main"}""");
        handler.EnqueueJson(HttpStatusCode.OK,
            """
            {"truncated":false,"tree":[
              {"path":"Denied.cs","type":"blob","size":10,"sha":"s"},
              {"path":"Open.cs","type":"blob","size":10,"sha":"s"}
            ]}
            """);
        // 403 without a zero rate-limit header is a per-file permission denial.
        handler.EnqueueJson(HttpStatusCode.Forbidden, "{}");
        handler.EnqueueJson(HttpStatusCode.OK, FileJson(Base64("open")));

        var snapshot = await fetcher.FetchAsync(Coords, CancellationToken.None);

        snapshot.Files.ShouldHaveSingleItem().Path.ShouldBe("Open.cs");
    }

    [Fact]
    public async Task Per_File_RateLimit_Still_Aborts_Whole_Fetch()
    {
        var (fetcher, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"full_name":"octocat/hello-world","default_branch":"main"}""");
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"truncated":false,"tree":[{"path":"Program.cs","type":"blob","size":10,"sha":"s"}]}""");
        // A 403 with X-RateLimit-Remaining: 0 is repo-wide and must not be skipped.
        handler.EnqueueJson(HttpStatusCode.Forbidden, "{}",
            r => r.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0"));

        var ex = await Should.ThrowAsync<CodeReviewFetchException>(
            () => fetcher.FetchAsync(Coords, CancellationToken.None));

        ex.Message.ShouldContain("rate limit");
    }

    [Fact]
    public async Task Empty_Tree_Returns_Snapshot_With_No_Files()
    {
        var (fetcher, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"full_name":"octocat/hello-world","default_branch":"main"}""");
        handler.EnqueueJson(HttpStatusCode.OK, """{"truncated":false,"tree":[]}""");

        var snapshot = await fetcher.FetchAsync(Coords, CancellationToken.None);

        snapshot.FullName.ShouldBe("octocat/hello-world");
        snapshot.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task All_Selected_Files_Binary_Returns_Empty_Snapshot()
    {
        var (fetcher, handler) = Build();
        var binary = Convert.ToBase64String(new byte[] { 0x00, 0x01 });
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"full_name":"octocat/hello-world","default_branch":"main"}""");
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"truncated":false,"tree":[{"path":"Program.cs","type":"blob","size":2,"sha":"s"}]}""");
        handler.EnqueueJson(HttpStatusCode.OK, $$"""{"content":"{{binary}}","encoding":"base64"}""");

        var snapshot = await fetcher.FetchAsync(Coords, CancellationToken.None);

        snapshot.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task Encodes_Branch_With_Slash_In_Tree_And_Ref()
    {
        var (fetcher, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"full_name":"octocat/hello-world","default_branch":"feature/new"}""");
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"truncated":false,"tree":[{"path":"Program.cs","type":"blob","size":10,"sha":"s"}]}""");
        handler.EnqueueJson(HttpStatusCode.OK, FileJson(Base64("class P {}")));

        await fetcher.FetchAsync(Coords, CancellationToken.None);

        // The slash in the branch name must be percent-encoded, not left to alter
        // the path or query.
        handler.RequestedPaths.ShouldContain(p => p.Contains("git/trees/feature%2Fnew"));
        handler.RequestedPaths.ShouldContain(p => p.Contains("ref=feature%2Fnew"));
    }

    [Fact]
    public async Task Applies_Selection_Cap()
    {
        var (fetcher, handler) = Build(maxFiles: 1);
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"full_name":"octocat/hello-world","default_branch":"main"}""");
        handler.EnqueueJson(HttpStatusCode.OK,
            """
            {"truncated":false,"tree":[
              {"path":"App.csproj","type":"blob","size":10,"sha":"s"},
              {"path":"Program.cs","type":"blob","size":10,"sha":"s"}
            ]}
            """);
        // Only one contents call should be made (the manifest ranks first).
        handler.EnqueueJson(HttpStatusCode.OK, FileJson(Base64("<Project/>")));

        var snapshot = await fetcher.FetchAsync(Coords, CancellationToken.None);

        snapshot.Files.ShouldHaveSingleItem().Path.ShouldBe("App.csproj");
    }

    [Fact]
    public async Task NotFound_Repo_Throws_Fetch_Exception()
    {
        var (fetcher, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.NotFound, "{}");

        var ex = await Should.ThrowAsync<CodeReviewFetchException>(
            () => fetcher.FetchAsync(Coords, CancellationToken.None));

        ex.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        ex.Message.ShouldContain("not found");
    }

    [Fact]
    public async Task RateLimited_Throws_RateLimit_Message()
    {
        var (fetcher, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.Forbidden, "{}",
            r => r.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0"));

        var ex = await Should.ThrowAsync<CodeReviewFetchException>(
            () => fetcher.FetchAsync(Coords, CancellationToken.None));

        ex.Message.ShouldContain("rate limit");
    }

    [Fact]
    public async Task ServerError_Throws_Unavailable_Message()
    {
        var (fetcher, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.InternalServerError, "{}");

        var ex = await Should.ThrowAsync<CodeReviewFetchException>(
            () => fetcher.FetchAsync(Coords, CancellationToken.None));

        ex.Message.ShouldContain("unavailable");
    }

    [Fact]
    public async Task Timeout_Propagates_As_TaskCanceled()
    {
        var (fetcher, handler) = Build();
        handler.EnqueueThrow(new TaskCanceledException("timed out"));

        await Should.ThrowAsync<TaskCanceledException>(
            () => fetcher.FetchAsync(Coords, CancellationToken.None));
    }

    [Fact]
    public async Task Transport_Failure_Propagates_As_HttpRequestException()
    {
        var (fetcher, handler) = Build();
        handler.EnqueueThrow(new HttpRequestException("connection reset"));

        await Should.ThrowAsync<HttpRequestException>(
            () => fetcher.FetchAsync(Coords, CancellationToken.None));
    }
}
