using System.Text;
using Microsoft.Extensions.Options;
using Scout.Plugin.CodeQA;
using Scout.Plugin.CodeQA.Indexing;

namespace Scout.Plugin.CodeQA.Tests.Indexing;

public class GitHubIngestorTests
{
    private const string Owner = "scout-org";
    private const string Repo = "scout";
    private const string Branch = "main";
    private const string HeadSha = "head-sha";

    private static IOptions<CodeQAOptions> BuildOptions(
        Action<CodeQAOptions>? configure = null)
    {
        var options = new CodeQAOptions
        {
            Repository = new RepositoryOptions
            {
                Owner = Owner,
                Name = Repo,
                Branch = Branch,
                TokenEnvVar = "SCOUT_GITHUB_TOKEN_TEST",
            },
            Ingestion = new IngestionOptions
            {
                IncludeGlobs = new List<string> { "**/*.cs", "**/*.md" },
                ExcludeGlobs = new List<string> { "**/bin/**", "**/obj/**" },
                MaxFileBytes = 200_000,
            },
        };
        configure?.Invoke(options);
        return Options.Create(options);
    }

    private static GitHubIngestor BuildIngestor(
        IGitHubTreeClient client,
        IOptions<CodeQAOptions>? options = null)
    {
        return new GitHubIngestor(options ?? BuildOptions(), client);
    }

    private static IGitHubTreeClient StubClient(
        GitTreeResponse tree,
        Dictionary<string, byte[]>? blobs = null)
    {
        blobs ??= new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var client = Substitute.For<IGitHubTreeClient>();
        client
            .GetBranchHeadAsync(Owner, Repo, Branch, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GitReference(HeadSha)));
        client
            .GetRecursiveTreeAsync(Owner, Repo, HeadSha, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(tree));
        client
            .GetBlobRawAsync(Owner, Repo, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var sha = ci.ArgAt<string>(2);
                if (blobs.TryGetValue(sha, out var bytes))
                {
                    return Task.FromResult(bytes);
                }
                throw new InvalidOperationException($"Unexpected blob sha '{sha}'.");
            });
        return client;
    }

    [Fact]
    public void Ctor_Null_Options_Throws()
    {
        var client = Substitute.For<IGitHubTreeClient>();
        Action act = () => _ = new GitHubIngestor(null!, client);
        act.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_Null_Client_Throws()
    {
        Action act = () => _ = new GitHubIngestor(BuildOptions(), null!);
        act.ShouldThrow<ArgumentNullException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Ctor_Rejects_Empty_Owner(string owner)
    {
        var options = BuildOptions(o => o.Repository.Owner = owner);
        var client = Substitute.For<IGitHubTreeClient>();

        Action act = () => _ = new GitHubIngestor(options, client);
        act.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void Ctor_Rejects_Empty_Include_Globs()
    {
        var options = BuildOptions(o => o.Ingestion.IncludeGlobs = new List<string>());
        var client = Substitute.For<IGitHubTreeClient>();

        Action act = () => _ = new GitHubIngestor(options, client);
        act.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void Ctor_Rejects_NonPositive_MaxFileBytes()
    {
        var options = BuildOptions(o => o.Ingestion.MaxFileBytes = 0);
        var client = Substitute.For<IGitHubTreeClient>();

        Action act = () => _ = new GitHubIngestor(options, client);
        act.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public async Task IngestAsync_Truncated_Tree_Throws()
    {
        var tree = new GitTreeResponse(Array.Empty<GitTreeEntry>(), Truncated: true);
        var ingestor = BuildIngestor(StubClient(tree));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => ingestor.IngestAsync(CancellationToken.None));
        ex.Message.ShouldContain("truncated");
    }

    [Fact]
    public async Task IngestAsync_Drops_Non_Blob_Entries()
    {
        var tree = new GitTreeResponse(
            new[]
            {
                new GitTreeEntry("src/", "tree-sha", 0, GitTreeEntryType.Tree),
                new GitTreeEntry("submodule", "commit-sha", 0, GitTreeEntryType.Commit),
                new GitTreeEntry("src/Foo.cs", "blob-1", 5, GitTreeEntryType.Blob),
            },
            Truncated: false);
        var blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["blob-1"] = Encoding.UTF8.GetBytes("hello"),
        };

        var result = await BuildIngestor(StubClient(tree, blobs))
            .IngestAsync(CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].Path.ShouldBe("src/Foo.cs");
    }

    [Fact]
    public async Task IngestAsync_Drops_Blobs_Over_Size_Cap_Without_Downloading()
    {
        var options = BuildOptions(o => o.Ingestion.MaxFileBytes = 10);
        var tree = new GitTreeResponse(
            new[]
            {
                new GitTreeEntry("big.cs", "big", 99, GitTreeEntryType.Blob),
                new GitTreeEntry("small.cs", "small", 5, GitTreeEntryType.Blob),
            },
            Truncated: false);
        var blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["small"] = Encoding.UTF8.GetBytes("hello"),
            // intentionally missing "big" — if the ingestor tried to fetch it, the stub would throw
        };

        var client = StubClient(tree, blobs);
        var ingestor = BuildIngestor(client, options);

        var result = await ingestor.IngestAsync(CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].Path.ShouldBe("small.cs");
        await client.DidNotReceive().GetBlobRawAsync(Owner, Repo, "big", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_Applies_Include_And_Exclude_Globs()
    {
        var tree = new GitTreeResponse(
            new[]
            {
                new GitTreeEntry("src/Foo.cs", "ok", 3, GitTreeEntryType.Blob),
                new GitTreeEntry("src/bin/Foo.cs", "excl-bin", 3, GitTreeEntryType.Blob),
                new GitTreeEntry("README.md", "md", 3, GitTreeEntryType.Blob),
                new GitTreeEntry("src/Foo.json", "other", 3, GitTreeEntryType.Blob),
            },
            Truncated: false);
        var blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["ok"] = Encoding.UTF8.GetBytes("a"),
            ["md"] = Encoding.UTF8.GetBytes("b"),
        };

        var result = await BuildIngestor(StubClient(tree, blobs))
            .IngestAsync(CancellationToken.None);

        result.Select(r => r.Path).ShouldBe(new[] { "README.md", "src/Foo.cs" });
    }

    [Fact]
    public async Task IngestAsync_Returns_Files_In_Ordinal_Path_Order()
    {
        var tree = new GitTreeResponse(
            new[]
            {
                new GitTreeEntry("z.cs", "z", 1, GitTreeEntryType.Blob),
                new GitTreeEntry("a.cs", "a", 1, GitTreeEntryType.Blob),
                new GitTreeEntry("B.cs", "B", 1, GitTreeEntryType.Blob),
            },
            Truncated: false);
        var blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["z"] = Encoding.UTF8.GetBytes("z"),
            ["a"] = Encoding.UTF8.GetBytes("a"),
            ["B"] = Encoding.UTF8.GetBytes("B"),
        };

        var result = await BuildIngestor(StubClient(tree, blobs))
            .IngestAsync(CancellationToken.None);

        // Ordinal sorts uppercase before lowercase.
        result.Select(r => r.Path).ShouldBe(new[] { "B.cs", "a.cs", "z.cs" });
    }

    [Fact]
    public async Task IngestAsync_Skips_Binary_Blobs_Via_NUL_Heuristic()
    {
        var tree = new GitTreeResponse(
            new[]
            {
                new GitTreeEntry("a.cs", "txt", 5, GitTreeEntryType.Blob),
                new GitTreeEntry("b.cs", "bin", 5, GitTreeEntryType.Blob),
            },
            Truncated: false);
        var blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["txt"] = Encoding.UTF8.GetBytes("hello"),
            ["bin"] = new byte[] { (byte)'a', 0, (byte)'b', (byte)'c' },
        };

        var result = await BuildIngestor(StubClient(tree, blobs))
            .IngestAsync(CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].Path.ShouldBe("a.cs");
    }

    [Fact]
    public async Task IngestAsync_Skips_Non_UTF8_Blobs()
    {
        var tree = new GitTreeResponse(
            new[]
            {
                new GitTreeEntry("good.cs", "good", 5, GitTreeEntryType.Blob),
                new GitTreeEntry("bad.cs", "bad", 3, GitTreeEntryType.Blob),
            },
            Truncated: false);
        var blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["good"] = Encoding.UTF8.GetBytes("hello"),
            // 0xFF is not a valid UTF-8 lead byte and also not NUL, so it
            // survives the binary heuristic but fails strict UTF-8 decode.
            ["bad"] = new byte[] { 0xFF, 0xFE, 0xFD },
        };

        var result = await BuildIngestor(StubClient(tree, blobs))
            .IngestAsync(CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].Path.ShouldBe("good.cs");
    }

    [Fact]
    public async Task IngestAsync_Preserves_Content_And_Size()
    {
        const string text = "public class Foo {}";
        var bytes = Encoding.UTF8.GetBytes(text);
        var tree = new GitTreeResponse(
            new[] { new GitTreeEntry("src/Foo.cs", "blob", bytes.Length, GitTreeEntryType.Blob) },
            Truncated: false);
        var blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["blob"] = bytes };

        var result = await BuildIngestor(StubClient(tree, blobs))
            .IngestAsync(CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].Content.ShouldBe(text);
        result[0].SizeBytes.ShouldBe(bytes.Length);
        result[0].Sha.ShouldBe("blob");
    }

    [Fact]
    public async Task IngestAsync_Honors_Cancellation_Before_First_Call()
    {
        var client = Substitute.For<IGitHubTreeClient>();
        var ingestor = new GitHubIngestor(BuildOptions(), client);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => ingestor.IngestAsync(cts.Token));

        await client.DidNotReceive().GetBranchHeadAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_Propagates_Client_Failures()
    {
        var client = Substitute.For<IGitHubTreeClient>();
        client
            .GetBranchHeadAsync(Owner, Repo, Branch, Arg.Any<CancellationToken>())
            .Returns<Task<GitReference>>(_ => throw new InvalidOperationException("boom"));

        var ingestor = new GitHubIngestor(BuildOptions(), client);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => ingestor.IngestAsync(CancellationToken.None));
        ex.Message.ShouldBe("boom");
    }

    [Fact]
    public async Task IngestAsync_Empty_Tree_Returns_Empty_List()
    {
        var tree = new GitTreeResponse(Array.Empty<GitTreeEntry>(), Truncated: false);

        var result = await BuildIngestor(StubClient(tree))
            .IngestAsync(CancellationToken.None);

        result.ShouldBeEmpty();
    }
}
