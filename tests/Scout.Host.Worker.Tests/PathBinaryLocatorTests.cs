using Scout.Host.Worker.Providers;
using Shouldly;

namespace Scout.Host.Worker.Tests;

public sealed class PathBinaryLocatorTests
{
    private static async Task<T> WithPathAsync<T>(string? path, Func<Task<T>> body)
    {
        var original = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", path);
            return await body().ConfigureAwait(false);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", original);
        }
    }

    [Fact]
    public async Task FindOnPathAsync_PathEmpty_ReturnsNull()
    {
        var locator = new PathBinaryLocator();

        var result = await WithPathAsync(string.Empty, async () =>
            await locator.FindOnPathAsync("ollama", CancellationToken.None));

        result.ShouldBeNull();
    }

    [Fact]
    public async Task FindOnPathAsync_BinaryAbsent_ReturnsNull()
    {
        var tempDir = Directory.CreateTempSubdirectory("scout-path-locator-").FullName;
        var locator = new PathBinaryLocator();
        try
        {
            var result = await WithPathAsync(tempDir, async () =>
                await locator.FindOnPathAsync("definitely-not-installed-binary", CancellationToken.None));

            result.ShouldBeNull();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task FindOnPathAsync_BinaryPresent_ReturnsResolvedPath()
    {
        var tempDir = Directory.CreateTempSubdirectory("scout-path-locator-").FullName;
        var binaryName = OperatingSystem.IsWindows() ? "ollama.exe" : "ollama";
        var binaryPath = Path.Combine(tempDir, binaryName);
        await File.WriteAllTextAsync(binaryPath, "#!/bin/sh\nexit 0\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                binaryPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var locator = new PathBinaryLocator();
        try
        {
            var result = await WithPathAsync(tempDir, async () =>
                await locator.FindOnPathAsync("ollama", CancellationToken.None));

            result.ShouldBe(binaryPath);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
