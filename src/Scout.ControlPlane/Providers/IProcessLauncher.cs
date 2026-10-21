namespace Scout.ControlPlane.Providers;

/// <summary>
/// Request describing a child process to spawn. Arguments are passed verbatim;
/// callers must escape them. Environment overrides are merged on top of the
/// inherited environment - a null value removes the variable.
/// </summary>
public sealed record ProcessLaunchRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string?>? EnvironmentOverrides = null);

/// <summary>
/// Handle to a running child process. Disposing terminates the process if it
/// is still alive.
/// </summary>
public interface ILaunchedProcess : IAsyncDisposable
{
    /// <summary>OS-level process identifier.</summary>
    int Pid { get; }

    /// <summary>True once the process has exited.</summary>
    bool HasExited { get; }

    /// <summary>Awaits process exit and returns the exit code.</summary>
    Task<int> WaitForExitAsync(CancellationToken ct);

    /// <summary>
    /// Attempts a graceful stop, then force-kills the process tree if the
    /// grace period elapses. Returns true when the process exited within
    /// <paramref name="graceful"/>, false when it had to be force-killed.
    /// </summary>
    Task<bool> StopAsync(TimeSpan graceful, CancellationToken ct);
}

/// <summary>
/// Spawns child processes. Tests substitute this to avoid forking real
/// binaries during unit tests of higher-level services.
/// </summary>
public interface IProcessLauncher
{
    /// <summary>Spawns the process described by <paramref name="request"/>.</summary>
    ILaunchedProcess Launch(ProcessLaunchRequest request);
}
