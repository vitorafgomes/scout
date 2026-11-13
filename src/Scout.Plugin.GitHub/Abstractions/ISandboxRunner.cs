namespace Scout.Plugin.GitHub.Abstractions;

/// <summary>
/// Runs a project's tests in a disposable container on the owner's machine, over a snapshot under Scout's data folder.
/// The GitHub plugin calls it for <c>run_pull_request_tests</c>; the host provides it, and without one the tests do not run.
/// </summary>
public interface ISandboxRunner
{
    /// <summary>Run the tests of the snapshot <paramref name="request"/> names and answer what happened.</summary>
    /// <param name="request">The snapshot, the stack hint and the command.</param>
    /// <param name="ct">Cancels the run.</param>
    /// <returns>Whether the tests ran, and how they ended or why they did not run.</returns>
    Task<SandboxRunResult> RunTestsAsync(SandboxRunRequest request, CancellationToken ct);
}

/// <summary>What to run in the container.</summary>
/// <param name="SnapshotRoot">Absolute path of the snapshot's root folder under Scout's data folder.</param>
/// <param name="StackHint">The stack the caller detected (for example <c>dotnet</c>, <c>node</c>, <c>python</c>, <c>go</c>), or null to let the runner detect it.</param>
/// <param name="Command">The test command the project names, or null for the stack's default.</param>
/// <param name="Repository">The repository as <c>owner/repo</c>, for the run's record; null when not known.</param>
/// <param name="PullRequest">The pull request number, for the run's record; null when not known.</param>
public sealed record SandboxRunRequest(string SnapshotRoot, string? StackHint, string? Command, string? Repository = null, int? PullRequest = null);

/// <summary>How a container run ended.</summary>
/// <param name="Ran">True when the test command ran to an exit code; false when nothing ran, and false too when the run passed the 20-minute limit, though its container ran.</param>
/// <param name="Reason">Why the tests did not run (including that the run passed the 20-minute limit), or why the restore failed (then <paramref name="Ran"/> is true and <paramref name="ExitCode"/> is the restore's); otherwise null.</param>
/// <param name="Image">The image the tests ran in, by digest; null when nothing ran (no snapshot, no stack, Docker missing or down), set when a run passed the time limit.</param>
/// <param name="Command">The command that ran; null when nothing ran (no snapshot, no stack, Docker missing or down), set when a run passed the time limit.</param>
/// <param name="ExitCode">The command's exit code; null when nothing ran.</param>
/// <param name="DurationSeconds">How long the run took, in seconds.</param>
/// <param name="Output">The captured output, possibly cut; empty when nothing ran.</param>
/// <param name="Note">What the owner should know about how it ran, such as the SDK it rolled forward to; null when nothing.</param>
public sealed record SandboxRunResult(
    bool Ran,
    string? Reason,
    string? Image,
    string? Command,
    int? ExitCode,
    double DurationSeconds,
    string Output,
    string? Note = null);
