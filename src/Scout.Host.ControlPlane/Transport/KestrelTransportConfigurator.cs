using System.Runtime.Versioning;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace Scout.Host.ControlPlane.Transport;

/// <summary>
/// Configures Kestrel to listen on a Unix domain socket (Linux/macOS) or a
/// named pipe (Windows) over h2c, never a TCP port.
/// </summary>
public static class KestrelTransportConfigurator
{
    /// <summary>
    /// Apply the per-OS bind. On Unix, runs the anti-hijack pre-flight before
    /// binding and returns the resolved socket path so the caller can chmod it
    /// to 0600 after the server starts. Returns null on Windows (named pipe).
    /// </summary>
    public static string? Configure(
        KestrelServerOptions kestrel,
        string? socketPathOverride,
        string pipeName,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(kestrel);

        if (OperatingSystem.IsWindows())
        {
            ConfigureNamedPipe(kestrel, pipeName);
            return null;
        }

        var socketPath = socketPathOverride ?? ControlPlaneEndpoint.ResolveSocketPath();
        ControlPlaneEndpoint.PreflightSocketPath(socketPath);

        kestrel.ListenUnixSocket(socketPath, listen => listen.Protocols = HttpProtocols.Http2);
        logger.LogInformation("Control plane listening on UDS {SocketPath} (h2c)", socketPath);
        return socketPath;
    }

    [SupportedOSPlatform("windows")]
    private static void ConfigureNamedPipe(KestrelServerOptions kestrel, string pipeName)
    {
        kestrel.ListenNamedPipe(pipeName, listen => listen.Protocols = HttpProtocols.Http2);
    }
}
