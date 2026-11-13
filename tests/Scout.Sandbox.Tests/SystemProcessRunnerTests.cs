using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Scout.Sandbox.Tests;

public sealed class SystemProcessRunnerTests
{
    private static async Task<(int Exit, string Output)> Run(ProcessCall call, CancellationToken ct = default)
    {
        var output = new StringBuilder();
        var exit = await SystemProcessRunner.Instance.RunAsync(call, chunk => { lock (output) { output.Append(chunk); } }, ct);
        return (exit, output.ToString());
    }

    [Fact]
    public async Task The_child_starts_with_only_the_environment_of_the_call()
    {
        Environment.GetEnvironmentVariable("PATH").ShouldNotBeNull();

        var (exit, output) = await Run(new ProcessCall("/usr/bin/env", [], new Dictionary<string, string> { ["ONLY_THIS"] = "1" }));

        exit.ShouldBe(0);
        output.Trim().ShouldBe("ONLY_THIS=1");
    }

    [Fact]
    public async Task Stdout_and_stderr_are_both_handed_over_and_the_exit_code_answered()
    {
        var (exit, output) = await Run(new ProcessCall("/bin/sh", ["-c", "echo to-out; echo to-err >&2; exit 7"], new Dictionary<string, string>()));

        exit.ShouldBe(7);
        output.ShouldContain("to-out");
        output.ShouldContain("to-err");
    }

    [Fact]
    public async Task A_cancelled_call_kills_the_child_and_throws()
    {
        using var cancel = new CancellationTokenSource();
        var pid = new TaskCompletionSource<int>();
        var call = new ProcessCall("/bin/sh", ["-c", "echo $$; exec sleep 30"], new Dictionary<string, string>());

        var run = SystemProcessRunner.Instance.RunAsync(call, chunk => pid.TrySetResult(int.Parse(chunk.Trim(), System.Globalization.CultureInfo.InvariantCulture)), cancel.Token);
        var child = await pid.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancel.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => run);
        var watch = Stopwatch.StartNew();
        while (IsAlive(child) && watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(50);
        }

        IsAlive(child).ShouldBeFalse($"the child {child} still runs after the call was cancelled");
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [Fact]
    public async Task An_executable_that_does_not_exist_throws_win32_exception()
    {
        await Should.ThrowAsync<Win32Exception>(() => Run(new ProcessCall("/nonexistent/docker", ["version"], new Dictionary<string, string>())));
    }
}
