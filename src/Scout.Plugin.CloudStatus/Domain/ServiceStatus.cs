namespace Scout.Plugin.CloudStatus.Domain;

/// <summary>
/// Health status of a single cloud service within a region. Validated at
/// construction.
/// </summary>
public sealed record ServiceStatus
{
    public ServiceStatus(
        string Service,
        string Region,
        HealthLevel Level,
        string? Message)
    {
        if (string.IsNullOrWhiteSpace(Service))
        {
            throw new ArgumentException("Service must be non-empty.", nameof(Service));
        }
        if (string.IsNullOrWhiteSpace(Region))
        {
            throw new ArgumentException("Region must be non-empty.", nameof(Region));
        }

        this.Service = Service;
        this.Region = Region;
        this.Level = Level;
        this.Message = Message;
    }

    public string Service { get; }
    public string Region { get; }
    public HealthLevel Level { get; }
    public string? Message { get; }
}
