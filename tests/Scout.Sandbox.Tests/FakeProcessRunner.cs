namespace Scout.Sandbox.Tests;

internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly List<ProcessCall> _calls = [];

    public Func<ProcessCall, Action<string>, CancellationToken, Task<int>> Behaviour { get; set; } = (_, _, _) => Task.FromResult(0);

    public IReadOnlyList<ProcessCall> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    public IReadOnlyList<ProcessCall> Runs => [.. Calls.Where(call => call.Arguments[0] == "run")];

    public ProcessCall Restore => Runs.Single(call => Name(call).EndsWith("-restore", StringComparison.Ordinal));

    public ProcessCall Test => Runs.Single(call => Name(call).EndsWith("-test", StringComparison.Ordinal));

    public Task<int> RunAsync(ProcessCall call, Action<string> onOutput, CancellationToken ct)
    {
        lock (_calls)
        {
            _calls.Add(call);
        }

        return Behaviour(call, onOutput, ct);
    }

    public static bool IsRun(ProcessCall call, string kind) =>
        call.Arguments[0] == "run" && Name(call).EndsWith($"-{kind}", StringComparison.Ordinal);

    public static string Name(ProcessCall call) => Value(call, "--name");

    public static string Value(ProcessCall call, string flag)
    {
        var index = call.Arguments.ToList().IndexOf(flag);
        index.ShouldBeGreaterThanOrEqualTo(0, $"{flag} is missing from: {string.Join(' ', call.Arguments)}");
        return call.Arguments[index + 1];
    }

    public static bool HasPair(ProcessCall call, string flag, string value) =>
        call.Arguments.Zip(call.Arguments.Skip(1)).Any(pair => pair.First == flag && pair.Second == value);

    public static string MountSource(ProcessCall call)
    {
        var mount = Value(call, "--mount");
        mount.ShouldStartWith("type=bind,source=");
        mount.ShouldEndWith(",target=/work");
        return mount["type=bind,source=".Length..^",target=/work".Length];
    }
}
