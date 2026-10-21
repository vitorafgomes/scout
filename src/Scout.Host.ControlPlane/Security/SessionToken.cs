using System.Security.Cryptography;

namespace Scout.Host.ControlPlane.Security;

/// <summary>
/// Per-session bearer token. The expected value is read from the
/// <c>SCOUT_CONTROLPLANE_TOKEN</c> environment variable when present
/// (handed in out-of-band by the Electron supervisor); otherwise a fresh
/// random token is minted at startup. The token is never logged.
/// </summary>
public sealed class SessionToken
{
    /// <summary>Environment variable carrying the expected token.</summary>
    public const string EnvVarName = "SCOUT_CONTROLPLANE_TOKEN";

    private readonly byte[] _expectedUtf8;

    private SessionToken(string value)
    {
        Value = value;
        _expectedUtf8 = System.Text.Encoding.UTF8.GetBytes(value);
    }

    /// <summary>The raw token value. Treat as a secret; never log it.</summary>
    public string Value { get; }

    /// <summary>
    /// Build the token from the environment, or mint a random 256-bit token
    /// when the variable is absent or blank.
    /// </summary>
    public static SessionToken FromEnvironmentOrMint()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvVarName);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return new SessionToken(fromEnv);
        }

        return new SessionToken(Mint());
    }

    /// <summary>Build a token with an explicit value (test seam).</summary>
    public static SessionToken FromValue(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new SessionToken(value);
    }

    /// <summary>
    /// Constant-time comparison of a presented token against the expected
    /// value. Returns false for null/empty input without leaking length via
    /// timing beyond what FixedTimeEquals guarantees.
    /// </summary>
    public bool Matches(string? presented)
    {
        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        var presentedUtf8 = System.Text.Encoding.UTF8.GetBytes(presented);
        return CryptographicOperations.FixedTimeEquals(presentedUtf8, _expectedUtf8);
    }

    private static string Mint()
    {
        Span<byte> buffer = stackalloc byte[32];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToHexString(buffer);
    }
}
