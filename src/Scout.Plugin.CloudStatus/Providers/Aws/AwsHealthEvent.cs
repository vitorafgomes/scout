namespace Scout.Plugin.CloudStatus.Providers.Aws;

/// <summary>
/// Minimum projection of an AWS Health event the provider needs. Decoupled
/// from any AWS SDK type so the source can be faked without pulling
/// SDK-specific testing helpers.
/// </summary>
/// <param name="Arn">AWS Health event ARN — stable id.</param>
/// <param name="Service">AWS service name, e.g. "EC2", "RDS".</param>
/// <param name="Region">Affected region, e.g. "us-east-1", "global".</param>
/// <param name="Category">
/// Raw event type category reported by AWS. Expected values include
/// <c>issue</c>, <c>accountNotification</c>, <c>scheduledChange</c>,
/// <c>investigation</c>. Unknown values are treated as degraded.
/// </param>
/// <param name="StatusCode">
/// AWS Health status code, expected values <c>open</c>, <c>closed</c>,
/// <c>upcoming</c>. Only <c>open</c> events contribute to health.
/// </param>
/// <param name="Description">Short human-readable description.</param>
public sealed record AwsHealthEvent(
    string Arn,
    string Service,
    string Region,
    string Category,
    string StatusCode,
    string Description);
