using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Scout.Host.Worker.Providers;
using Shouldly;

namespace Scout.Host.Worker.Tests;

public sealed class SystemProcessLauncherTests
{
    private static SystemProcessLauncher CreateLauncher() => new(NullLoggerFactory.Instance);

    [Fact]
    public async Task Launch_RealEcho_ExitsWithZero()
    {
        if (OperatingSystem.IsWindows())
        {
            // The Windows equivalent is not always on PATH; this test is
            // primarily exercised on the Linux CI image.
            return;
        }

        var launcher = CreateLauncher();
        await using var process = launcher.Launch(
            new ProcessLaunchRequest("echo", new[] { "hello" }));

        var exit = await process.WaitForExitAsync(CancellationToken.None);
        exit.ShouldBe(0);
        process.HasExited.ShouldBeTrue();
    }

    [Fact]
    public void Launch_BadBinary_ThrowsWin32()
    {
        var launcher = CreateLauncher();

        // Win32Exception is what Process.Start raises on every supported
        // platform when the file is not found / not executable.
        Should.Throw<Win32Exception>(() =>
            launcher.Launch(new ProcessLaunchRequest(
                "/definitely/not/a/real/binary-" + Guid.NewGuid().ToString("N"),
                Array.Empty<string>())));
    }

    [Fact]
    public async Task Launch_LongLived_StopAsyncGraceful_ReturnsTrue()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var launcher = CreateLauncher();
        await using var process = launcher.Launch(
            new ProcessLaunchRequest("sleep", new[] { "30" }));

        var stopped = await process.StopAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        stopped.ShouldBeTrue();
        process.HasExited.ShouldBeTrue();
    }

    [Fact]
    public async Task Launch_LongLived_StopAsyncForceKill_ReturnsFalse()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var launcher = CreateLauncher();
        // Trap SIGTERM so the graceful path expires and the launcher has
        // to fall back to force-kill.
        await using var process = launcher.Launch(new ProcessLaunchRequest(
            "bash",
            new[] { "-c", "trap '' TERM; sleep 30" }));

        var stopped = await process.StopAsync(TimeSpan.FromMilliseconds(500), CancellationToken.None);

        stopped.ShouldBeFalse();
        process.HasExited.ShouldBeTrue();
    }

    [Fact]
    public async Task Launch_DisposeBeforeExit_KillsProcess()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var launcher = CreateLauncher();
        int pid;
        {
            var process = launcher.Launch(new ProcessLaunchRequest("sleep", new[] { "30" }));
            pid = process.Pid;
            await process.DisposeAsync();
        }

        // Give the kernel a moment to reap the entry, then confirm the
        // process is no longer alive.
        var stillAlive = TryGetProcess(pid);
        stillAlive.ShouldBeFalse();
    }

    private static bool TryGetProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            // GetProcessById succeeds for zombie entries too; consult HasExited.
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
