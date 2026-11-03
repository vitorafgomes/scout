using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Features;
using Scout.ControlPlane.Operator;
using Scout.ControlPlane.WriteGate;
using Scout.Host.ControlPlane.Services;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Operator;

/// <summary>The control plane's own timer that clears old research, over Scout's files in a temp folder.</summary>
public sealed class ResearchCleanupServiceTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("scout-cleanup-").FullName;
    private readonly JsonFileFeatureStateStore _states;
    private readonly JsonFileActionQueue _actions;
    private readonly JsonLinesWriteLog _writes;
    private readonly JsonFileOperatorStore _store;
    private readonly RecordingLogger<ResearchCleanupService> _logger = new();
    private readonly ResearchCleanupService _cleanup;

    public ResearchCleanupServiceTests()
    {
        var options = Options.Create(new FeatureStateStoreOptions { FilePath = Path.Combine(_directory, "features.json") });
        _states = new JsonFileFeatureStateStore(options, NullLogger<JsonFileFeatureStateStore>.Instance);
        _actions = new JsonFileActionQueue(options, NullLogger<JsonFileActionQueue>.Instance);
        _writes = new JsonLinesWriteLog(options, NullLogger<JsonLinesWriteLog>.Instance);
        _store = new JsonFileOperatorStore(options, NullLogger<JsonFileOperatorStore>.Instance);
        var service = new OperatorService(_store, new WriteGateService(new FeatureCatalog(), _states, _actions, _writes), _writes);
        _cleanup = new ResearchCleanupService(service, _logger);
    }

    public void Dispose()
    {
        _states.Dispose();
        _actions.Dispose();
        _writes.Dispose();
        _store.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task A_Run_That_Cannot_Write_Is_Logged_And_The_Next_Run_Clears_The_Old_Candidates()
    {
        await _store.UpdateProjectsAsync(
            projects =>
            {
                projects.Add(new OperatorProject(
                    Guid.NewGuid().ToString("N"), "Why rivers meander", "approved", "desktop", DateTimeOffset.UtcNow.AddDays(-40),
                    DateTimeOffset.UtcNow, [new TopicCandidate("Rivers", "Fits", 0.9)], null, []));
                return 0;
            },
            CancellationToken.None);
        Directory.CreateDirectory(_store.ProjectsPath + ".tmp");

        await _cleanup.RunOnceAsync(CancellationToken.None);
        var afterFailure = (await _store.ReadProjectsAsync(CancellationToken.None)).Single().Candidates.Count;
        Directory.Delete(_store.ProjectsPath + ".tmp");
        await _cleanup.RunOnceAsync(CancellationToken.None);

        afterFailure.ShouldBe(1);
        _logger.Entries.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            e => e.Level.ShouldBe(LogLevel.Warning),
            e => e.Message.ShouldStartWith("Old research could not be cleared; the next run tries again"));
        (await _store.ReadProjectsAsync(CancellationToken.None)).Single().Candidates.ShouldBeEmpty();
    }
}
