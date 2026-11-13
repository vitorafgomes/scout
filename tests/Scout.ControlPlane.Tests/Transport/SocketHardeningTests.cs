using System.Net.Sockets;
using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scout.Host.ControlPlane.Transport;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Transport;

public sealed class SocketHardeningTests
{
    private const string SocketPath = "/run/user/1000/scout/controlplane.sock";

    [Fact]
    public void A_Socket_That_Cannot_Be_Hardened_Logs_Critical_Sets_A_Non_Zero_Exit_Code_And_Stops_The_Host()
    {
        var logger = new RecordingLogger<SocketHardeningTests>();
        var lifetime = new ApplicationLifetime(NullLogger<ApplicationLifetime>.Instance);
        var previous = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;

            ControlPlaneEndpoint.HardenSocketOrStop(
                SocketPath,
                _ => throw new UnauthorizedAccessException("Access to the path is denied."),
                logger,
                lifetime);

            logger.Entries.ShouldBe([
                (LogLevel.Critical,
                    $"The control plane socket {SocketPath} could not be restricted to its owner; the control plane is stopping."),
            ]);
            lifetime.ApplicationStopping.IsCancellationRequested.ShouldBeTrue();
            Environment.ExitCode.ShouldBe(1);
        }
        finally
        {
            Environment.ExitCode = previous;
        }
    }

    [Fact]
    public void A_Socket_That_Is_Hardened_Logs_Nothing_Keeps_The_Exit_Code_And_Keeps_The_Host_Running()
    {
        var logger = new RecordingLogger<SocketHardeningTests>();
        var lifetime = new ApplicationLifetime(NullLogger<ApplicationLifetime>.Instance);
        var hardened = new List<string>();
        var previous = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;

            ControlPlaneEndpoint.HardenSocketOrStop(SocketPath, hardened.Add, logger, lifetime);

            hardened.ShouldBe([SocketPath]);
            logger.Entries.ShouldBeEmpty();
            lifetime.ApplicationStopping.IsCancellationRequested.ShouldBeFalse();
            Environment.ExitCode.ShouldBe(0);
        }
        finally
        {
            Environment.ExitCode = previous;
        }
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public void HardenSocket_Leaves_A_Bound_Socket_Readable_And_Writable_By_Its_Owner_Only()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Directory.CreateTempSubdirectory("scout-harden-").FullName;
        var path = Path.Combine(dir, "controlplane.sock");
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Bind(new UnixDomainSocketEndPoint(path));
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                    | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);

            ControlPlaneEndpoint.HardenSocket(path);

            File.GetUnixFileMode(path).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
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
    public void HardenSocket_Fails_When_The_Socket_Cannot_Be_Found_Instead_Of_Skipping_The_Chmod()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Directory.CreateTempSubdirectory("scout-harden-missing-").FullName;
        var path = Path.Combine(dir, "controlplane.sock");
        try
        {
            var ex = Should.Throw<InvalidOperationException>(() => ControlPlaneEndpoint.HardenSocket(path));

            ex.Message.ShouldBe($"Cannot restrict '{path}' to its owner: it is missing or its ownership cannot be verified.");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
