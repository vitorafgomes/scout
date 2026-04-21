using System.ComponentModel;
using Microsoft.SemanticKernel;
using Scout.Plugin.CloudStatus.Domain;
using Scout.Plugin.CloudStatus.Providers;

namespace Scout.Plugin.CloudStatus;

/// <summary>
/// Semantic Kernel functions exposed by <see cref="CloudStatusPlugin"/>.
/// All functions are read-only and aggregate results across the registered
/// <see cref="ICloudHealthProvider"/> instances.
/// </summary>
public sealed class CloudStatusFunctions
{
    private readonly IReadOnlyList<ICloudHealthProvider> _providers;

    public CloudStatusFunctions(IEnumerable<ICloudHealthProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.ToArray();
    }

    [KernelFunction("get_all_cloud_health")]
    [Description("Returns the current health report for every configured cloud provider.")]
    public async Task<IReadOnlyList<StatusReport>> GetAllAsync(CancellationToken cancellationToken)
    {
        if (_providers.Count == 0)
        {
            return Array.Empty<StatusReport>();
        }

        var tasks = _providers.Select(p => SafeGetHealthAsync(p, cancellationToken)).ToArray();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    [KernelFunction("get_cloud_health")]
    [Description("Returns the current health report for a single cloud provider (e.g. Azure, AWS, OracleCloud).")]
    public async Task<StatusReport> GetAsync(
        [Description("Provider name such as Azure, AWS, or OracleCloud.")] string provider,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        var match = _providers.FirstOrDefault(
            p => string.Equals(p.Name, provider, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            return StatusReport.Unknown(
                provider,
                scope: "(not configured)",
                checkedAt: DateTimeOffset.UtcNow,
                message: $"No '{provider}' provider is registered.");
        }

        return await SafeGetHealthAsync(match, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <see cref="ICloudHealthProvider.GetHealthAsync"/> and converts any
    /// unexpected failure into an <see cref="HealthLevel.Unknown"/> report so
    /// one misbehaving provider cannot break the aggregate call.
    /// </summary>
    private static async Task<StatusReport> SafeGetHealthAsync(
        ICloudHealthProvider provider,
        CancellationToken cancellationToken)
    {
        try
        {
            return await provider.GetHealthAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return StatusReport.Unknown(
                provider.Name,
                scope: "(error)",
                checkedAt: DateTimeOffset.UtcNow,
                message: ex.Message);
        }
    }
}
