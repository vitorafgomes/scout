using Scout.Plugin.CloudStatus.Domain;

namespace Scout.Plugin.CloudStatus.Providers;

/// <summary>
/// Deterministic provider used in tests and as a safe default when no real
/// cloud credentials are configured. Returns whatever <see cref="StatusReport"/>
/// the caller provides at construction. Exists in the production assembly so
/// consumer apps can opt into a demo mode without pulling the test project.
/// </summary>
public sealed class InMemoryCloudHealthProvider : ICloudHealthProvider
{
    private readonly StatusReport _report;

    public InMemoryCloudHealthProvider(StatusReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        _report = report;
    }

    public string Name => _report.Provider;

    public Task<StatusReport> GetHealthAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_report);
}
