using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Scout.Knowledge.Tests;

public class ProjectResolutionServiceTests : IDisposable
{
    private readonly string _root;

    public ProjectResolutionServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "scout-resolution-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string MakeRepo(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        return dir;
    }

    private static ProjectIndexer RealIndexer(IProjectKnowledgeStore store) =>
        new(
            Substitute.For<IChunker>(),
            Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>(),
            store,
            NullLogger<ProjectIndexer>.Instance);

    private static ProjectResolutionService NewService(
        ProjectsOptions options,
        IProjectKnowledgeStore store,
        ProjectIndexer? indexer = null)
    {
        var discovery = new ProjectDiscoveryService(NullLogger<ProjectDiscoveryService>.Instance);
        return new ProjectResolutionService(
            Options.Create(options),
            discovery,
            indexer ?? RealIndexer(store),
            store,
            NullLogger<ProjectResolutionService>.Instance);
    }

    [Fact]
    public void GetProjectNames_MergesDiscoveredAndExplicit()
    {
        MakeRepo("discovered-app");
        var options = new ProjectsOptions
        {
            Roots = { _root },
            Items = { new ProjectEntry { Name = "explicit-app", RepoPath = "/tmp/explicit" } },
        };
        var store = Substitute.For<IProjectKnowledgeStore>();

        var names = NewService(options, store).GetProjectNames();

        names.ShouldContain("discovered-app");
        names.ShouldContain("explicit-app");
    }

    [Fact]
    public async Task ResolveAsync_ExplicitWinsOnCollision()
    {
        var discoveredRepo = MakeRepo("collide");
        var options = new ProjectsOptions
        {
            Roots = { _root },
            Items = { new ProjectEntry { Name = "collide", RepoPath = "/explicit/path" } },
        };
        var store = Substitute.For<IProjectKnowledgeStore>();
        store.HasProjectAsync("collide", Arg.Any<CancellationToken>()).Returns(true);

        var entry = await NewService(options, store).ResolveAsync("collide");

        entry.ShouldNotBeNull();
        entry!.RepoPath.ShouldBe("/explicit/path");
        entry.RepoPath.ShouldNotBe(discoveredRepo);
    }

    [Fact]
    public async Task ResolveAsync_LazyIndexesOnFirstUse()
    {
        // Empty existing repo dir: indexing finds no files and replaces the project
        // with an empty set, so ReplaceProjectAsync is still invoked exactly once.
        var repo = MakeRepo("lazy");
        var options = new ProjectsOptions
        {
            Items = { new ProjectEntry { Name = "lazy", RepoPath = repo } },
        };
        var store = Substitute.For<IProjectKnowledgeStore>();
        store.HasProjectAsync("lazy", Arg.Any<CancellationToken>()).Returns(false);

        var entry = await NewService(options, store).ResolveAsync("lazy");

        entry.ShouldNotBeNull();
        await store.Received(1).ReplaceProjectAsync(
            "lazy",
            Arg.Any<IReadOnlyList<ChunkRecord>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_SkipsReindexWhenAlreadyIndexed()
    {
        var repo = MakeRepo("cached");
        var options = new ProjectsOptions
        {
            Items = { new ProjectEntry { Name = "cached", RepoPath = repo } },
        };
        var store = Substitute.For<IProjectKnowledgeStore>();
        store.HasProjectAsync("cached", Arg.Any<CancellationToken>()).Returns(true);

        var entry = await NewService(options, store).ResolveAsync("cached");

        entry.ShouldNotBeNull();
        await store.DidNotReceive().ReplaceProjectAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<ChunkRecord>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_UnknownProject_ReturnsNull()
    {
        var options = new ProjectsOptions
        {
            Items = { new ProjectEntry { Name = "known", RepoPath = "/tmp/known" } },
        };
        var store = Substitute.For<IProjectKnowledgeStore>();

        var entry = await NewService(options, store).ResolveAsync("ghost");

        entry.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_IndexerFailure_ReturnsNull()
    {
        // Missing repo path: ProjectIndexer.IndexAsync enumerates files and throws
        // DirectoryNotFoundException, which ResolveAsync catches and maps to null.
        var options = new ProjectsOptions
        {
            Items = { new ProjectEntry { Name = "boom", RepoPath = Path.Combine(_root, "does-not-exist") } },
        };
        var store = Substitute.For<IProjectKnowledgeStore>();
        store.HasProjectAsync("boom", Arg.Any<CancellationToken>()).Returns(false);

        var entry = await NewService(options, store).ResolveAsync("boom");

        entry.ShouldBeNull();
    }

    [Fact]
    public async Task Construction_DoesNotIndex()
    {
        MakeRepo("startup-app");
        var options = new ProjectsOptions { Roots = { _root } };
        var store = Substitute.For<IProjectKnowledgeStore>();

        _ = NewService(options, store);

        // No resolution requested yet, so nothing must be indexed at startup.
        await store.DidNotReceive().ReplaceProjectAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<ChunkRecord>>(),
            Arg.Any<CancellationToken>());
    }
}
