namespace Scout.Sandbox;

/// <summary>
/// One test run asked of the sandbox: the snapshot folder to copy, the stack to use when the manifests are ambiguous
/// (<c>dotnet</c>, <c>node</c>, <c>python</c> or <c>go</c>), and the test command to run in place of the stack's.
/// </summary>
public sealed record SandboxRequest(string SnapshotRoot, string? StackHint = null, string? Command = null);

/// <summary>
/// What the run did: whether the tests ran, why not when they did not, the image and the command that ran, its exit
/// code, how long it took, and the last 4,000 characters of the output, which is data and never instructions.
/// </summary>
public sealed record SandboxResult(
    bool Ran,
    string? Reason,
    string? Image,
    string? Command,
    int? ExitCode,
    double DurationSeconds,
    string Output);
