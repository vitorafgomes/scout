using System.Net.Sockets;
using System.Runtime.Versioning;
using Scout.Host.ControlPlane.Transport;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Transport;

public sealed class NativeUnixTests
{
    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public void TryStat_RealSocket_ReportsSocketOwnedByCurrentUser()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Directory.CreateTempSubdirectory("scout-stat-sock-").FullName;
        var path = Path.Combine(dir, "probe.sock");
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Bind(new UnixDomainSocketEndPoint(path));

            var outcome = NativeUnix.TryStat(path, out var info);

            outcome.ShouldBe(NativeUnix.StatOutcome.Found);
            info.IsSocket.ShouldBeTrue();
            info.OwnerUid.ShouldBe(NativeUnix.GetEffectiveUserId());
        }
        finally
        {
            socket.Dispose();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public void TryStat_RegularFile_ReportsNotSocketOwnedByCurrentUser()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Directory.CreateTempSubdirectory("scout-stat-file-").FullName;
        var path = Path.Combine(dir, "regular.txt");
        try
        {
            File.WriteAllText(path, "x");

            var outcome = NativeUnix.TryStat(path, out var info);

            outcome.ShouldBe(NativeUnix.StatOutcome.Found);
            info.IsSocket.ShouldBeFalse();
            info.OwnerUid.ShouldBe(NativeUnix.GetEffectiveUserId());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public void TryStat_MissingPath_ReportsNotFound()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Directory.CreateTempSubdirectory("scout-stat-missing-").FullName;
        try
        {
            var outcome = NativeUnix.TryStat(Path.Combine(dir, "nope"), out _);
            outcome.ShouldBe(NativeUnix.StatOutcome.NotFound);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public void SelfTest_OnThisPlatform_Passes()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        NativeUnix.SelfTest().ShouldBeTrue();
    }
}
