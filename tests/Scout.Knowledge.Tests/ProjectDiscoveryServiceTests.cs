using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Scout.Knowledge.Tests;

public sealed class ProjectDiscoveryServiceTests : IDisposable
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
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public async Task DiscoverAsync_On_An_Existing_Root_That_Cannot_Be_Read_Returns_Empty_And_Logs_One_Warning()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
        {
            return;
        }

        var repo = MakeChild("hidden-repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var logger = new RecordingLogger();
        var original = File.GetUnixFileMode(_root);
        File.SetUnixFileMode(_root, UnixFileMode.None);
        try
        {
            Directory.Exists(_root).ShouldBeTrue();

            var result = await new ProjectDiscoveryService(logger).DiscoverAsync(_root);

            result.ShouldBeEmpty();
            var warning = logger.Entries.ShouldHaveSingleItem();
            warning.Level.ShouldBe(LogLevel.Warning);
            warning.Message.ShouldBe($"Failed to enumerate discovery root: {_root}");
            warning.Exception.ShouldBeOfType<UnauthorizedAccessException>();
        }
        finally
        {
            File.SetUnixFileMode(_root, original);
        }
    }

    public static TheoryData<Exception> ListingFailures =>
    [
        new IOException("The listing broke while it was being read."),
        new UnauthorizedAccessException("The listing was refused while it was being read."),
    ];

    [Theory]
    [MemberData(nameof(ListingFailures))]
    public async Task An_Error_While_A_Roots_Entries_Are_Read_Returns_An_Empty_List_With_A_Warning(Exception failure)
    {
        var repo = MakeChild("first-repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var logger = new RecordingLogger();
        var service = new ProjectDiscoveryService(logger) { ListDirectories = _ => OneFolderThenFailure(repo, failure) };

        var result = await service.DiscoverAsync(_root);

        result.ShouldBeEmpty();
        var warning = logger.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldBe($"Failed to enumerate discovery root: {_root}");
        warning.Exception.ShouldBeSameAs(failure);
    }

    private static IEnumerable<string> OneFolderThenFailure(string folder, Exception failure)
    {
        yield return folder;
        throw failure;
    }

    private sealed class RecordingLogger : ILogger<ProjectDiscoveryService>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
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
