namespace Scout.Sandbox;

/// <summary>
/// One test run asked of the sandbox: the snapshot folder to copy, the stack to use when the manifests are ambiguous
/// (<c>dotnet</c>, <c>node</c>, <c>python</c>, <c>go</c> or <c>java</c>), the test command to run in place of the
/// stack's, and the repository (<c>owner/repo</c>) and pull request number the run's record names, when known.
/// </summary>
public sealed record SandboxRequest(string SnapshotRoot, string? StackHint = null, string? Command = null, string? Repository = null, int? PullRequest = null);

/// <summary>
/// What the run did: whether the tests ran, why not when they did not, the image and the command that ran, its exit
/// code, how long it took, the last 4,000 characters of the output (on a failed run led by the lines found before them
/// that name the failure), which is data and never instructions, and a note on how the stack ran: the runtime or SDK
/// version that ran and whether the project declared it, which file asked for it, or that nothing was declared and the
/// default ran (the Java runs also name the Maven or Gradle that ran).
/// </summary>
public sealed record SandboxResult(
    bool Ran,
    string? Reason,
    string? Image,
    string? Command,
    int? ExitCode,
    double DurationSeconds,
    string Output,
    string? Note = null);

/// <summary>One version a repository declares: the file that declares it and the version it asks for, such as <c>.nvmrc</c> and <c>Node 20</c>.</summary>
public sealed record SandboxDeclaredVersion(string Source, string Version);

/// <summary>
/// What the sandbox would run a snapshot on, read from its manifests as text and never by running the build: the stack,
/// the pinned image, the tool and version in that image, and the versions the repository declares; or, when none would
/// run, the stack that refused (null when no manifest was found) and the sentence that says why.
/// </summary>
public sealed record SandboxDetection(
    string? Stack,
    string? Image,
    string? Runs,
    IReadOnlyList<SandboxDeclaredVersion> Declared,
    string? Refusal);
