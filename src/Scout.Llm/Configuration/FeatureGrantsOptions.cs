namespace Scout.Llm.Configuration;

/// <summary>
/// The features whose write permission the owner granted in the desktop. Bound
/// from "Scout:Features"; the desktop sets <c>SCOUT__FEATURES__GRANTEDWRITES</c>
/// when it starts the worker.
/// </summary>
public sealed class FeatureGrantsOptions
{
    /// <summary>Configuration section bound to these options.</summary>
    public const string Section = "Scout:Features";

    /// <summary>Comma-separated feature ids whose write grant is on.</summary>
    public string GrantedWrites { get; set; } = string.Empty;

    /// <summary>The ids in <see cref="GrantedWrites"/>, trimmed, without empty entries.</summary>
    public IReadOnlyList<string> GrantedWriteIds() =>
        GrantedWrites.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
