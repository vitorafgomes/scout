namespace Scout.Llm.Configuration;

/// <summary>
/// Settings for the read-only guard backed by TypeSafe's Jev model. Bound from
/// "Scout:Guard:TypeSafe". An empty <see cref="ApiKey"/> leaves the guard off.
/// </summary>
public sealed class TypeSafeGuardOptions
{
    /// <summary>Configuration section bound to these options.</summary>
    public const string Section = "Scout:Guard:TypeSafe";

    /// <summary>TypeSafe API key. Empty disables the guard.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>TypeSafe model identifier.</summary>
    public string Model { get; set; } = "jev-latest";

    /// <summary>Base URL of the TypeSafe API.</summary>
    public string Endpoint { get; set; } = "https://api.typesafe.ai";

    /// <summary>Probability, 0 to 1, at or above which a request is refused.</summary>
    public double Threshold { get; set; } = 0.5;
}
