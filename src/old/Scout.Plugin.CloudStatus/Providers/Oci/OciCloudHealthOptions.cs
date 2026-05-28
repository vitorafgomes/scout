namespace Scout.Plugin.CloudStatus.Providers.Oci;

/// <summary>
/// Settings for <see cref="OciCloudHealthProvider"/>. Bound from
/// "Scout:Plugins:CloudStatus:OracleCloud". Oracle Cloud's status page is
/// public — no credentials are ever required.
/// </summary>
public sealed class OciCloudHealthOptions
{
    /// <summary>
    /// Full URL of the OCI status endpoint returning the expected JSON
    /// schema. Defaults to the public Oracle Cloud status page. Override
    /// if Oracle changes the endpoint or if a proxy is required.
    /// </summary>
    public string StatusEndpoint { get; set; } =
        "https://ocistatus.oraclecloud.com/api/v2/summary.json";

    /// <summary>
    /// Optional whitelist of region slugs. Empty = no filter (report all
    /// regions). Matching is case-insensitive.
    /// </summary>
    public IList<string> Regions { get; set; } = new List<string>();

    /// <summary>
    /// HTTP timeout per request. Low default — the status endpoint returns
    /// a small payload, and the bot should not block a chat reply for long.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);
}
