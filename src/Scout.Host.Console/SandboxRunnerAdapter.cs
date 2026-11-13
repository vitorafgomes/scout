using Scout.Plugin.GitHub.Abstractions;
using Scout.Sandbox;

namespace Scout.Host.Console;

/// <summary>
/// The GitHub plugin's <see cref="ISandboxRunner"/> over <see cref="DockerSandboxRunner"/>: the request and the result
/// copied field by field, so neither library references the other. While the owner has Docker runs off in Scout Desktop
/// (<c>Scout:Sandbox:Enabled</c> not true) it answers that nothing ran and never calls the runner.
/// </summary>
internal sealed class SandboxRunnerAdapter(DockerSandboxRunner runner, bool enabled) : ISandboxRunner
{
    /// <summary>The configuration key Scout Desktop sets to true when the owner turns Docker runs on.</summary>
    internal const string EnabledKey = "Scout:Sandbox:Enabled";

    /// <summary>The reason a run gives while Docker runs are off.</summary>
    internal const string RunsOff = "Docker runs are off in Scout Desktop; turn them on in Features, Docker.";

    public async Task<SandboxRunResult> RunTestsAsync(SandboxRunRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!enabled)
        {
            return new SandboxRunResult(false, RunsOff, null, null, null, 0, string.Empty, null);
        }

        var result = await runner.RunTestsAsync(new SandboxRequest(request.SnapshotRoot, request.StackHint, request.Command, request.Repository, request.PullRequest), ct).ConfigureAwait(false);
        return new SandboxRunResult(result.Ran, result.Reason, result.Image, result.Command, result.ExitCode, result.DurationSeconds, result.Output, result.Note);
    }
}
