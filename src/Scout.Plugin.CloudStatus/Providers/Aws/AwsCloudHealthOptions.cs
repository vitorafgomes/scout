namespace Scout.Plugin.CloudStatus.Providers.Aws;

/// <summary>
/// Settings for <see cref="AwsCloudHealthProvider"/>. Bound from
/// "Scout:Plugins:CloudStatus:Aws". Credentials follow the standard AWS
/// credential chain (env vars, shared profile, IAM role, SSO) — never inline.
/// </summary>
public sealed class AwsCloudHealthOptions
{
    /// <summary>Primary region used to resolve the AWS Health endpoint.</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>Optional shared-credentials profile name.</summary>
    public string? Profile { get; set; }
}
