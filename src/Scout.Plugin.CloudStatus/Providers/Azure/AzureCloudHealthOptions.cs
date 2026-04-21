namespace Scout.Plugin.CloudStatus.Providers.Azure;

/// <summary>
/// Settings for <see cref="AzureCloudHealthProvider"/>. Bound from
/// "Scout:Plugins:CloudStatus:Azure". Credentials are never carried inline —
/// use the standard Azure credential chain (<see cref="global::Azure.Identity.DefaultAzureCredential"/>)
/// via environment variables, managed identity, User Secrets, or az login.
/// </summary>
public sealed class AzureCloudHealthOptions
{
    /// <summary>Target subscription id. Required.</summary>
    public string SubscriptionId { get; set; } = string.Empty;

    /// <summary>Optional tenant id to scope <c>DefaultAzureCredential</c>.</summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// If both <see cref="ClientId"/> and <see cref="ClientSecretEnvVar"/> are set,
    /// use <see cref="global::Azure.Identity.ClientSecretCredential"/> and resolve
    /// the secret from the named environment variable. Otherwise fall back to
    /// <see cref="global::Azure.Identity.DefaultAzureCredential"/>.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>Name of the environment variable that holds the client secret.</summary>
    public string? ClientSecretEnvVar { get; set; }
}
