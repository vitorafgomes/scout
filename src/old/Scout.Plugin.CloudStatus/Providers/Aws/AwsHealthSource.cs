using Amazon;
using Amazon.AWSHealth;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Microsoft.Extensions.Options;

namespace Scout.Plugin.CloudStatus.Providers.Aws;

/// <summary>
/// Default <see cref="IAwsHealthSource"/>. Builds an <see cref="AmazonAWSHealthClient"/>
/// with the configured region and optional named credentials profile, so
/// authentication and endpoint resolution are in place for the first real query.
///
/// <para>
/// The live <c>DescribeEvents</c> call is tracked as a follow-up issue in
/// the source repository. Today <see cref="GetOpenEventsAsync"/> returns an
/// empty list, which <see cref="AwsCloudHealthProvider"/> maps to
/// <see cref="Domain.HealthLevel.Healthy"/>. The seam keeps the swap
/// isolated — the live SDK call can land without touching
/// <see cref="AwsCloudHealthProvider"/> or its tests.
/// </para>
/// </summary>
public sealed class AwsHealthSource : IAwsHealthSource, IDisposable
{
    private readonly AmazonAWSHealthClient _client;

    public AwsHealthSource(IOptions<AwsCloudHealthOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Value);

        var region = RegionEndpoint.GetBySystemName(options.Value.Region);
        var credentials = ResolveCredentials(options.Value.Profile);

        _client = credentials is null
            ? new AmazonAWSHealthClient(region)
            : new AmazonAWSHealthClient(credentials, region);
    }

    public Task<IReadOnlyList<AwsHealthEvent>> GetOpenEventsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = _client;
        return Task.FromResult<IReadOnlyList<AwsHealthEvent>>(Array.Empty<AwsHealthEvent>());
    }

    public void Dispose() => _client.Dispose();

    private static AWSCredentials? ResolveCredentials(string? profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            return null;
        }

        var chain = new CredentialProfileStoreChain();
        return chain.TryGetAWSCredentials(profileName, out var credentials)
            ? credentials
            : null;
    }
}
