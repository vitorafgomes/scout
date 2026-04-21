using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Microsoft.Extensions.Options;

namespace Scout.Plugin.CloudStatus.Providers.Azure;

/// <summary>
/// Default <see cref="IAzureHealthSource"/>. Builds an <see cref="ArmClient"/>
/// from <see cref="AzureCloudHealthOptions"/> — explicit
/// <see cref="ClientSecretCredential"/> when both <c>ClientId</c> and
/// <c>ClientSecretEnvVar</c> are set, otherwise
/// <see cref="DefaultAzureCredential"/> — so the credential chain is in
/// place for the first real query.
///
/// <para>
/// The actual Service Health events call is tracked as a follow-up issue.
/// Today <see cref="GetActiveEventsAsync"/> returns an empty list, which
/// <see cref="AzureCloudHealthProvider"/> maps to
/// <see cref="Domain.HealthLevel.Healthy"/>. The seam is real; swapping the
/// body for the live SDK call lands in an isolated follow-up PR and does
/// not touch <see cref="AzureCloudHealthProvider"/> or its tests.
/// </para>
/// </summary>
public sealed class AzureHealthSource : IAzureHealthSource
{
    private readonly ArmClient _client;

    public AzureHealthSource(
        IOptions<AzureCloudHealthOptions> options,
        TokenCredential? credentialOverride = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Value);

        if (string.IsNullOrWhiteSpace(options.Value.SubscriptionId))
        {
            throw new ArgumentException(
                "AzureCloudHealthOptions.SubscriptionId must be configured.",
                nameof(options));
        }

        var credential = credentialOverride ?? BuildCredential(options.Value);
        _client = new ArmClient(credential);
    }

    public Task<IReadOnlyList<AzureHealthEvent>> GetActiveEventsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = _client;
        return Task.FromResult<IReadOnlyList<AzureHealthEvent>>(Array.Empty<AzureHealthEvent>());
    }

    private static TokenCredential BuildCredential(AzureCloudHealthOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ClientId) &&
            !string.IsNullOrWhiteSpace(options.ClientSecretEnvVar) &&
            !string.IsNullOrWhiteSpace(options.TenantId))
        {
            var secret = Environment.GetEnvironmentVariable(options.ClientSecretEnvVar!);
            if (!string.IsNullOrWhiteSpace(secret))
            {
                return new ClientSecretCredential(
                    tenantId: options.TenantId!,
                    clientId: options.ClientId!,
                    clientSecret: secret);
            }
        }

        var credentialOptions = new DefaultAzureCredentialOptions();
        if (!string.IsNullOrWhiteSpace(options.TenantId))
        {
            credentialOptions.TenantId = options.TenantId;
        }
        return new DefaultAzureCredential(credentialOptions);
    }
}
