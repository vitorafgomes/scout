using Microsoft.Extensions.Logging.Abstractions;

namespace Scout.Knowledge.Tests;

public class ProjectDiscoveryServiceTests : IDisposable
{
    private readonly string _root;

    public ProjectDiscoveryServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "scout-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static ProjectDiscoveryService NewService() =>
        new(NullLogger<ProjectDiscoveryService>.Instance);

    private string MakeChild(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task DiscoverAsync_DetectsGitRepo()
    {
        var repo = MakeChild("git-repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));

        var result = await NewService().DiscoverAsync(_root);

        result.ShouldContain(e => e.Name == "git-repo" && e.RepoPath == repo);
    }

    [Theory]
    [InlineData("app.csproj")]
    [InlineData("app.sln")]
    [InlineData("package.json")]
    [InlineData("go.mod")]
    [InlineData("pyproject.toml")]
    [InlineData("Cargo.toml")]
    public async Task DiscoverAsync_DetectsMarkerFiles(string marker)
    {
        var repo = MakeChild("marker-repo");
        File.WriteAllText(Path.Combine(repo, marker), "x");

        var result = await NewService().DiscoverAsync(_root);

        result.ShouldContain(e => e.Name == "marker-repo");
    }

    [Fact]
    public async Task DiscoverAsync_IgnoresNonRepoDirectory()
    {
        MakeChild("just-a-folder");

        var result = await NewService().DiscoverAsync(_root);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task DiscoverAsync_MissingRoot_ReturnsEmpty()
    {
        var result = await NewService().DiscoverAsync(Path.Combine(_root, "does-not-exist"));

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task DiscoverAsync_NullOrEmptyRoot_ReturnsEmpty()
    {
        (await NewService().DiscoverAsync(null)).ShouldBeEmpty();
        (await NewService().DiscoverAsync("")).ShouldBeEmpty();
        (await NewService().DiscoverAsync("   ")).ShouldBeEmpty();
    }

    [Fact]
    public async Task DiscoverAsync_NamesAreDirectoryNames()
    {
        var repo = MakeChild("my-app");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));

        var result = await NewService().DiscoverAsync(_root);

        result.Single().Name.ShouldBe("my-app");
    }
}
