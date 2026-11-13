namespace Scout.Plugin.GitHub.Abstractions;

/// <summary>
/// Reads which stack a repository snapshot would run its tests on, from its manifests as text and never by running the
/// build. The GitHub plugin asks it each time a snapshot is taken or reused and keeps the answer beside the snapshots;
/// the host provides it, and without one nothing is recorded.
/// </summary>
public interface IStackDetector
{
    /// <summary>The stack the snapshot at <paramref name="snapshotRoot"/> would run on, or why none would.</summary>
    /// <param name="snapshotRoot">Absolute path of the snapshot's root folder under Scout's data folder.</param>
    /// <returns>The stack, the image and the versions, or the refusal.</returns>
    RepositoryStack Detect(string snapshotRoot);
}

/// <summary>What a test run of a repository would use, or why none would run.</summary>
/// <param name="Stack">The stack (.NET, Node, Python, Go or Java), also when it refused; null when no manifest was found.</param>
/// <param name="Image">The pinned image a run would use, by digest; null when none would run.</param>
/// <param name="Runs">The tool and version in that image, such as <c>Node 20.20.2</c>; null when none would run.</param>
/// <param name="Declared">Each version the repository declares, with the file that declares it; empty when it declares none.</param>
/// <param name="Refusal">Why no run would happen; null when one would.</param>
public sealed record RepositoryStack(string? Stack, string? Image, string? Runs, IReadOnlyList<DeclaredVersion> Declared, string? Refusal);

/// <summary>One version a repository declares.</summary>
/// <param name="Source">The file that declares it, such as <c>.nvmrc</c>.</param>
/// <param name="Version">The version it asks for, such as <c>Node 20</c>.</param>
public sealed record DeclaredVersion(string Source, string Version);
