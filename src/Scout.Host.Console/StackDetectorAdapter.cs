using Scout.Plugin.GitHub.Abstractions;
using Scout.Sandbox;

namespace Scout.Host.Console;

/// <summary>
/// The GitHub plugin's <see cref="IStackDetector"/> over the sandbox's own detection, copied field by field so neither
/// library references the other. It reads the snapshot's manifests only; it runs nothing, whether Docker runs are on or off.
/// </summary>
internal sealed class StackDetectorAdapter : IStackDetector
{
    public RepositoryStack Detect(string snapshotRoot)
    {
        var found = DockerSandboxRunner.Detect(snapshotRoot);
        return new RepositoryStack(found.Stack, found.Image, found.Runs, [.. found.Declared.Select(d => new DeclaredVersion(d.Source, d.Version))], found.Refusal);
    }
}
