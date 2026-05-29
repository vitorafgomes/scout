namespace Scout.ControlPlane.Providers;

/// <summary>
/// Resolves a binary name against the current process PATH. Implementations
/// must not consult platform-specific fallback directories.
/// </summary>
public interface IBinaryLocator
{
    /// <summary>
    /// Returns the absolute path of <paramref name="binaryName"/> if it is
    /// found on PATH, or null otherwise. The lookup honours the platform's
    /// executable suffix conventions (e.g. <c>.exe</c> on Windows).
    /// </summary>
    Task<string?> FindOnPathAsync(string binaryName, CancellationToken ct);
}
