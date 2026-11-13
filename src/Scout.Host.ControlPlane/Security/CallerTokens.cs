namespace Scout.Host.ControlPlane.Security;

/// <summary>
/// The two callers of the control plane: the desktop, which holds the session
/// token, and the worker, which holds its own token from
/// <c>SCOUT_CONTROLPLANE_WORKER_TOKEN</c> and may call the write gate only.
/// Without a worker token the write gate refuses every caller. Neither token is
/// ever logged.
/// </summary>
public sealed class CallerTokens
{
    /// <summary>Environment variable carrying the worker's token.</summary>
    public const string WorkerEnvVarName = "SCOUT_CONTROLPLANE_WORKER_TOKEN";

    private CallerTokens(SessionToken desktop, SessionToken? worker)
    {
        Desktop = desktop;
        Worker = worker;
    }

    /// <summary>The desktop's session token.</summary>
    public SessionToken Desktop { get; }

    /// <summary>The worker's token, or null when none was handed in.</summary>
    public SessionToken? Worker { get; }

    /// <summary>
    /// Pair the desktop token with the worker token value. A blank worker token
    /// leaves the worker role empty and logs one warning.
    /// </summary>
    /// <exception cref="InvalidOperationException">The worker token equals the desktop token.</exception>
    public static CallerTokens Create(SessionToken desktop, string? worker, ILogger<CallerTokens> logger)
    {
        ArgumentNullException.ThrowIfNull(desktop);
        ArgumentNullException.ThrowIfNull(logger);

        if (string.IsNullOrWhiteSpace(worker))
        {
            logger.LogWarning(
                "{EnvVar} is not set; the WorkerGate service refuses every caller, so the worker cannot request a write.",
                WorkerEnvVarName);
            return new CallerTokens(desktop, null);
        }

        if (desktop.Matches(worker))
        {
            throw new InvalidOperationException(
                "The control plane's worker token equals its desktop token; refusing to start because the worker would gain the desktop's calls.");
        }

        return new CallerTokens(desktop, SessionToken.FromValue(worker));
    }
}
