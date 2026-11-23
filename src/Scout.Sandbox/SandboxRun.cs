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
/// default ran (the Java runs also name the Maven or Gradle that ran); and, when the tests that failed could not run
/// because the container has no Docker daemon, the sentence that says so with what passed, null otherwise; and the test
/// projects' counts as data when the output holds <c>dotnet test</c>'s summaries, null otherwise.
/// </summary>
public sealed record SandboxResult(
    bool Ran,
    string? Reason,
    string? Image,
    string? Command,
    int? ExitCode,
    double DurationSeconds,
    string Output,
    string? Note = null,
    string? Unrunnable = null,
    SandboxTestCounts? Tests = null);

/// <summary>
/// One test project's summary line of a <c>dotnet test</c> run: its name, its passed, failed and skipped tests, and whether
/// its failures could not run for want of a Docker daemon: it failed, passed no test, and every failure of it the output
/// kept names a missing daemon.
/// </summary>
public sealed record SandboxTestProject(string Name, long Passed, long Failed, long Skipped, bool Unrunnable);

/// <summary>
/// The test projects' summaries a run printed, read from the whole output as it arrived; <c>Partial</c> when more projects
/// printed a summary than Scout counts, so the later ones are not among them; and the sentence that says what passed,
/// what failed and where, and what could not run.
/// </summary>
public sealed record SandboxTestCounts(IReadOnlyList<SandboxTestProject> Projects, bool Partial, string Sentence);

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
