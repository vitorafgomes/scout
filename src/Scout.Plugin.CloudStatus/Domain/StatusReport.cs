namespace Scout.Plugin.CloudStatus.Domain;

/// <summary>
/// Aggregate health report for one provider in one scope (Azure subscription,
/// AWS account, OCI region, etc.). Immutable, validated at construction.
/// </summary>
public sealed record StatusReport
{
    public StatusReport(
        string Provider,
        string Scope,
        HealthLevel Overall,
        IReadOnlyList<ServiceStatus> Services,
        DateTimeOffset CheckedAt,
        string? Message = null)
    {
        if (string.IsNullOrWhiteSpace(Provider))
        {
            throw new ArgumentException("Provider must be non-empty.", nameof(Provider));
        }
        if (string.IsNullOrWhiteSpace(Scope))
        {
            throw new ArgumentException("Scope must be non-empty.", nameof(Scope));
        }
        ArgumentNullException.ThrowIfNull(Services);

        this.Provider = Provider;
        this.Scope = Scope;
        this.Overall = Overall;
        this.Services = Services;
        this.CheckedAt = CheckedAt;
        this.Message = Message;
    }

    public string Provider { get; }
    public string Scope { get; }
    public HealthLevel Overall { get; }
    public IReadOnlyList<ServiceStatus> Services { get; }
    public DateTimeOffset CheckedAt { get; }

    /// <summary>Optional free-form context (e.g. why the result is Unknown).</summary>
    public string? Message { get; }

    /// <summary>
    /// Build an <see cref="HealthLevel.Unknown"/> report when the provider
    /// could not be queried — e.g. missing credentials, network failure.
    /// </summary>
    public static StatusReport Unknown(
        string provider,
        string scope,
        DateTimeOffset checkedAt,
        string? message = null) =>
        new(
            Provider: provider,
            Scope: scope,
            Overall: HealthLevel.Unknown,
            Services: Array.Empty<ServiceStatus>(),
            CheckedAt: checkedAt,
            Message: message);
}
