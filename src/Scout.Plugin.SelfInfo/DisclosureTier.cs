namespace Scout.Plugin.SelfInfo;

/// <summary>
/// Output disclosure level resolved per call from the ambient caller identity.
/// Gates OUTPUT DISCLOSURE only; it never authorizes a write.
/// </summary>
internal enum DisclosureTier
{
    /// <summary>
    /// Public-safe output only: the byte-for-byte pre-Path-B response, safe for the
    /// least-privileged workspace member. The fail-closed default.
    /// </summary>
    Public,

    /// <summary>
    /// Public-safe output plus the survey's "medium" tier (real install/config
    /// paths, machine name, exact versions/counts, knowledge-project names). The
    /// "high/never" tier (secrets, hostname, IPs, endpoints, OS build) stays
    /// redacted even here.
    /// </summary>
    Elevated,
}
