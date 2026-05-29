using System.Runtime.Versioning;
using Scout.Host.ControlPlane.Transport;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Transport;

public sealed class ControlPlaneEndpointTests
{
    [Fact]
    public void UseNamedPipe_MatchesCurrentOs()
    {
        ControlPlaneEndpoint.UseNamedPipe.ShouldBe(OperatingSystem.IsWindows());
    }

    [Fact]
    public void ResolveSocketPath_HonoursXdgRuntimeDir()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var tempDir = Directory.CreateTempSubdirectory("scout-xdg-").FullName;
        var previous = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        try
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", tempDir);
            var path = ControlPlaneEndpoint.ResolveSocketPath();
            path.ShouldStartWith(Path.Combine(tempDir, "scout"));
            path.ShouldEndWith(ControlPlaneEndpoint.SocketFileName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", previous);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public void Preflight_CleanPath_DoesNotThrow()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Directory.CreateTempSubdirectory("scout-clean-").FullName;
        try
        {
            var path = Path.Combine(dir, "controlplane.sock");
            Should.NotThrow(() => ControlPlaneEndpoint.PreflightSocketPath(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public void Preflight_ExistingNonSocket_Refuses()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Directory.CreateTempSubdirectory("scout-squat-").FullName;
        try
        {
            var path = Path.Combine(dir, "controlplane.sock");
            File.WriteAllText(path, "not a socket");

            var ex = Should.Throw<InvalidOperationException>(
                () => ControlPlaneEndpoint.PreflightSocketPath(path));
            ex.Message.ShouldContain("not a socket");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
