using System.Runtime.Versioning;

namespace Scout.Host.ControlPlane.Transport;

/// <summary>
/// Resolves the per-OS transport endpoint for the control plane and performs
/// the Unix-domain-socket anti-hijack pre-flight. No TCP port is ever opened.
/// </summary>
public static class ControlPlaneEndpoint
{
    /// <summary>Default named-pipe name on Windows.</summary>
    public const string DefaultPipeName = "scout-controlplane";

    /// <summary>Socket file name under the runtime directory on Unix.</summary>
    public const string SocketFileName = "controlplane.sock";

    /// <summary>True when the host should bind a Windows named pipe.</summary>
    public static bool UseNamedPipe => OperatingSystem.IsWindows();

    /// <summary>
    /// Compute the UDS path under <c>$XDG_RUNTIME_DIR</c>, falling back to a
    /// per-user 0700 directory under the temp folder when the runtime
    /// directory is not set (e.g. headless macOS, some containers).
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    public static string ResolveSocketPath()
    {
        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        string baseDir;
        if (!string.IsNullOrWhiteSpace(runtimeDir) && Directory.Exists(runtimeDir))
        {
            baseDir = Path.Combine(runtimeDir, "scout");
        }
        else
        {
            // Fallback: a stable per-uid directory under temp, locked to 0700.
            baseDir = Path.Combine(Path.GetTempPath(), $"scout-{NativeUnix.GetEffectiveUserId()}");
        }

        Directory.CreateDirectory(baseDir);
        File.SetUnixFileMode(
            baseDir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return Path.Combine(baseDir, SocketFileName);
    }

    /// <summary>
    /// Anti-hijack pre-flight for a UDS path. If something already exists at
    /// the path: refuse (throw) unless it is a socket owned by the current
    /// uid; when it is our own stale socket, delete it so the listener can
    /// recreate it. After binding, the caller must call <see cref="HardenSocket"/>.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    public static void PreflightSocketPath(string socketPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);

        var outcome = NativeUnix.TryStat(socketPath, out var info);
        if (outcome == NativeUnix.StatOutcome.NotFound)
        {
            // Nothing there: clean slate.
            return;
        }

        if (outcome == NativeUnix.StatOutcome.Unavailable)
        {
            // Cannot verify ownership of an existing path. Fail closed: refuse
            // to bind rather than risk binding over an attacker-planted socket.
            throw new InvalidOperationException(
                $"Refusing to bind: cannot verify ownership of '{socketPath}' (stat unavailable on this platform).");
        }

        if (!info.IsSocket)
        {
            // A regular file / dir / symlink squats the path. Refuse rather
            // than delete: removing an attacker-planted file would be a
            // foot-gun, and a legitimate non-socket here is a config error.
            throw new InvalidOperationException(
                $"Refusing to bind: '{socketPath}' exists and is not a socket.");
        }

        var self = NativeUnix.GetEffectiveUserId();
        if (info.OwnerUid != self)
        {
            throw new InvalidOperationException(
                $"Refusing to bind: socket '{socketPath}' is owned by uid {info.OwnerUid}, not {self}.");
        }

        // Our own stale socket from a previous run: safe to remove + recreate.
        File.Delete(socketPath);
    }

    /// <summary>
    /// Tighten the socket file to 0600 once Kestrel has created it. Throws when
    /// the socket is missing or cannot be verified, so it is never left as
    /// Kestrel created it.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    public static void HardenSocket(string socketPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        if (NativeUnix.TryStat(socketPath, out _) != NativeUnix.StatOutcome.Found)
        {
            throw new InvalidOperationException(
                $"Cannot restrict '{socketPath}' to its owner: it is missing or its ownership cannot be verified.");
        }

        File.SetUnixFileMode(
            socketPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>
    /// Runs <paramref name="harden"/> on the socket and, when it fails, logs
    /// the failure as critical, sets a non-zero exit code and stops the
    /// application: the control plane never keeps serving on a socket it could
    /// not restrict to its owner.
    /// </summary>
    internal static void HardenSocketOrStop(
        string socketPath,
        Action<string> harden,
        ILogger logger,
        IHostApplicationLifetime lifetime)
    {
        try
        {
            harden(socketPath);
        }
        catch (Exception ex)
        {
            logger.LogCritical(
                ex,
                "The control plane socket {SocketPath} could not be restricted to its owner; the control plane is stopping.",
                socketPath);
            Environment.ExitCode = 1;
            lifetime.StopApplication();
        }
    }
}
