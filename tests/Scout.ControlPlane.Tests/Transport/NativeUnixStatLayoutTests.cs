using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Scout.Host.ControlPlane.Transport;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Transport;

public sealed class NativeUnixStatLayoutTests
{
    private const uint SocketMode = 0xC000 | 0x180;
    private const uint RegularFileMode = 0x8000 | 0x1A4;

    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public void An_Arm64_Stat_Is_Read_With_The_Mode_At_16_And_The_Owner_At_24()
    {
        var socket = NativeUnix.ReadLinuxStat(Arm64Stat(SocketMode, uid: 1000, gid: 2000), Architecture.Arm64);
        var file = NativeUnix.ReadLinuxStat(Arm64Stat(RegularFileMode, uid: 1000, gid: 2000), Architecture.Arm64);

        socket.ShouldBe(new NativeUnix.StatInfo(1000, IsSocket: true, IsRegularFile: false));
        file.ShouldBe(new NativeUnix.StatInfo(1000, IsSocket: false, IsRegularFile: true));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public void An_X64_Stat_Is_Read_With_The_Mode_At_24_And_The_Owner_At_28()
    {
        var socket = NativeUnix.ReadLinuxStat(X64Stat(SocketMode, uid: 1000, gid: 2000), Architecture.X64);
        var file = NativeUnix.ReadLinuxStat(X64Stat(RegularFileMode, uid: 1000, gid: 2000), Architecture.X64);

        socket.ShouldBe(new NativeUnix.StatInfo(1000, IsSocket: true, IsRegularFile: false));
        file.ShouldBe(new NativeUnix.StatInfo(1000, IsSocket: false, IsRegularFile: true));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public void An_Architecture_Without_Known_Offsets_Is_Refused()
    {
        var ex = Should.Throw<ArgumentOutOfRangeException>(
            () => NativeUnix.ReadLinuxStat(X64Stat(RegularFileMode, uid: 1000, gid: 2000), Architecture.X86));

        ex.ParamName.ShouldBe("architecture");
        ex.ActualValue.ShouldBe(Architecture.X86);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public void The_Self_Test_Rejects_A_Probe_Read_At_The_Wrong_Offsets_Even_When_The_Owner_Happens_To_Match()
    {
        var arm64File = Arm64Stat(RegularFileMode, uid: 1000, gid: 1000);
        var misread = NativeUnix.ReadLinuxStat(arm64File, Architecture.X64);
        var read = NativeUnix.ReadLinuxStat(arm64File, Architecture.Arm64);

        misread.OwnerUid.ShouldBe(1000);
        NativeUnix.IsTrustedProbe(misread, effectiveUserId: 1000).ShouldBeFalse();
        NativeUnix.IsTrustedProbe(read, effectiveUserId: 1000).ShouldBeTrue();
        NativeUnix.IsTrustedProbe(read, effectiveUserId: 1001).ShouldBeFalse();
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public void A_Stat_That_Fails_For_Another_Reason_Than_A_Missing_Path_Is_Unavailable_And_The_Preflight_Refuses_To_Bind()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Directory.CreateTempSubdirectory("scout-stat-enotdir-").FullName;
        var blocker = Path.Combine(dir, "blocker");
        var path = Path.Combine(blocker, "controlplane.sock");
        try
        {
            File.WriteAllText(blocker, "a regular file where a directory is expected");

            var outcome = NativeUnix.TryStat(path, out _);
            var ex = Should.Throw<InvalidOperationException>(() => ControlPlaneEndpoint.PreflightSocketPath(path));

            outcome.ShouldBe(NativeUnix.StatOutcome.Unavailable);
            ex.Message.ShouldBe($"Refusing to bind: cannot verify ownership of '{path}' (stat unavailable on this platform).");
            File.ReadAllText(blocker).ShouldBe("a regular file where a directory is expected");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static byte[] X64Stat(uint mode, uint uid, uint gid)
    {
        var buffer = new byte[144];
        BitConverter.TryWriteBytes(buffer.AsSpan(16), 1UL).ShouldBeTrue();
        BitConverter.TryWriteBytes(buffer.AsSpan(24), mode).ShouldBeTrue();
        BitConverter.TryWriteBytes(buffer.AsSpan(28), uid).ShouldBeTrue();
        BitConverter.TryWriteBytes(buffer.AsSpan(32), gid).ShouldBeTrue();
        return buffer;
    }

    private static byte[] Arm64Stat(uint mode, uint uid, uint gid)
    {
        var buffer = new byte[144];
        BitConverter.TryWriteBytes(buffer.AsSpan(16), mode).ShouldBeTrue();
        BitConverter.TryWriteBytes(buffer.AsSpan(20), 1U).ShouldBeTrue();
        BitConverter.TryWriteBytes(buffer.AsSpan(24), uid).ShouldBeTrue();
        BitConverter.TryWriteBytes(buffer.AsSpan(28), gid).ShouldBeTrue();
        return buffer;
    }
}
