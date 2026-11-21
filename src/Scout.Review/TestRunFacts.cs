namespace Scout.Review;

/// <summary>One test project of a run: its name, its passed, failed and skipped tests, and whether its failures could not run for want of a Docker daemon.</summary>
/// <param name="Name">The test project, such as the assembly's name without <c>.dll</c>.</param>
/// <param name="Passed">Its tests that passed.</param>
/// <param name="Failed">Its tests that failed, or could not run when <paramref name="CouldNotRun"/>.</param>
/// <param name="Skipped">Its tests that were skipped.</param>
/// <param name="CouldNotRun">True when every failure of it needs a Docker daemon the container does not have.</param>
public sealed record TestProjectFacts(string Name, long Passed, long Failed, long Skipped, bool CouldNotRun);

/// <summary>
/// What a run of a pull request's tests established, as data for the review: whether the tests ran, why not, the command,
/// its exit code, the test projects whose summary the run printed, and whether earlier projects' summaries were not kept.
/// </summary>
/// <param name="Ran">True when the test command ran to an exit code.</param>
/// <param name="Reason">Why the tests did not run, or why the restore failed; null otherwise.</param>
/// <param name="Command">The command that ran; null when none did.</param>
/// <param name="ExitCode">The command's exit code; null when none ran.</param>
/// <param name="Projects">The test projects whose summary the run printed; empty when it printed none or its runner's summaries are not read.</param>
/// <param name="Partial">True when more test projects printed a summary than are counted, so the later ones are not among <paramref name="Projects"/>.</param>
public sealed record TestRunFacts(bool Ran, string? Reason, string? Command, int? ExitCode, IReadOnlyList<TestProjectFacts> Projects, bool Partial)
{
    /// <summary>
    /// True when at least one test project printed its summary, since a project whose tests ran was built; null otherwise,
    /// since a build that failed and a run that printed no summary read the same: never false.
    /// </summary>
    public bool? Built => Projects.Count > 0 ? true : null;
}
