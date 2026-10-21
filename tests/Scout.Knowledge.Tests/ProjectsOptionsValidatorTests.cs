using Microsoft.Extensions.Logging;
using NSubstitute;
using Scout.Knowledge;

namespace Scout.Knowledge.Tests;

/// <summary>
/// Verifies the configured-projects validator flags malformed entries with a
/// warning each and never throws, so a mis-configured projects list degrades
/// gracefully instead of failing the host silently.
/// </summary>
public sealed class ProjectsOptionsValidatorTests
{
    private static string ExistingDirectory() => AppContext.BaseDirectory;

    private static (ProjectsOptionsValidator Validator, ILogger<ProjectsOptionsValidator> Logger) Create()
    {
        var logger = Substitute.For<ILogger<ProjectsOptionsValidator>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        return (new ProjectsOptionsValidator(logger), logger);
    }

    private static int WarningCount(ILogger logger) => logger.ReceivedCalls()
        .Count(c => c.GetMethodInfo().Name == nameof(ILogger.Log)
            && (LogLevel)c.GetArguments()[0]! == LogLevel.Warning);

    [Fact]
    public void Validate_FlagsEmptyName()
    {
        var (validator, logger) = Create();
        var options = new ProjectsOptions
        {
            Items = { new ProjectEntry { Name = "   ", RepoPath = ExistingDirectory() } },
        };

        validator.Validate(options);

        WarningCount(logger).ShouldBe(1);
    }

    [Fact]
    public void Validate_FlagsEmptyRepoPath()
    {
        var (validator, logger) = Create();
        var options = new ProjectsOptions
        {
            Items = { new ProjectEntry { Name = "scout", RepoPath = " " } },
        };

        validator.Validate(options);

        WarningCount(logger).ShouldBe(1);
    }

    [Fact]
    public void Validate_FlagsNonExistentRepoPath()
    {
        var (validator, logger) = Create();
        var options = new ProjectsOptions
        {
            Items = { new ProjectEntry { Name = "scout", RepoPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) } },
        };

        validator.Validate(options);

        WarningCount(logger).ShouldBe(1);
    }

    [Fact]
    public void Validate_PassesValidEntry()
    {
        var (validator, logger) = Create();
        var options = new ProjectsOptions
        {
            Items = { new ProjectEntry { Name = "scout", RepoPath = ExistingDirectory() } },
        };

        validator.Validate(options);

        WarningCount(logger).ShouldBe(0);
    }

    [Fact]
    public void Validate_DoesNotThrow_OnMultipleMalformedEntries()
    {
        var (validator, logger) = Create();
        var options = new ProjectsOptions
        {
            Items =
            {
                new ProjectEntry { Name = string.Empty, RepoPath = string.Empty },
                new ProjectEntry { Name = "ok", RepoPath = ExistingDirectory() },
            },
        };

        Should.NotThrow(() => validator.Validate(options));

        // Two issues on the first entry (empty name and empty path), none on the second.
        WarningCount(logger).ShouldBe(2);
    }
}
