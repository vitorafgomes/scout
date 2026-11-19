namespace Scout.Audit;

/// <summary>
/// The checks Scout's code ran before a pull request review, each with its outcome and the sentence the owner reads; kept
/// once on the review's job when it starts, so the card shows what was true then. Read only from Scout's own facts and
/// the start's arguments, never from the model's words or the pull request's text.
/// </summary>
/// <param name="Checks">Every check, in the order it ran.</param>
public sealed record ReviewPreflight(IReadOnlyList<PreflightCheck> Checks)
{
    /// <summary>No check ran: the start was refused on its arguments, or it resumed a review already kept with its own checks.</summary>
    internal static readonly ReviewPreflight None = new([]);
}

/// <summary>
/// One check before a review: its name, its outcome (<see cref="PreflightOutcome"/>) and the sentence the owner reads. A
/// check whose source could not be read is a warning whose sentence starts "Could not check".
/// </summary>
/// <param name="Name">The check's name.</param>
/// <param name="Outcome">Ok, a warning or a block.</param>
/// <param name="Sentence">What the owner reads.</param>
public sealed record PreflightCheck(string Name, string Outcome, string Sentence);

/// <summary>
/// The outcomes of a check: ok, a warning, which starts the review and stays on it, and a block, which starts none.
/// </summary>
public static class PreflightOutcome
{
    /// <summary>The check passed.</summary>
    public const string Ok = "ok";

    /// <summary>The review starts, and the sentence stays on it.</summary>
    public const string Warning = "warning";

    /// <summary>The review does not start.</summary>
    public const string Block = "block";
}

/// <summary>The names of the checks before a review, in the order they run.</summary>
internal static class PreflightName
{
    internal const string Size = "Size";
    internal const string DockerRuns = "Docker runs";
    internal const string Head = "Head";
    internal const string Duplicate = "Duplicate";
    internal const string Grants = "Grants";
}

/// <summary>What a review start did: whether it started or resumed the review, the sentence the owner reads, and the checks it ran.</summary>
/// <param name="Started">Whether the review started or resumed.</param>
/// <param name="Sentence">What the owner reads; on a block, the blocks' sentences.</param>
/// <param name="Preflight">The checks it ran.</param>
public sealed record ReviewStart(bool Started, string Sentence, ReviewPreflight Preflight);

/// <summary>The head commit's checks as the caller read them: how many passed, how many are pending and how many failed.</summary>
/// <param name="Passing">Checks that passed, were skipped or neutral.</param>
/// <param name="Pending">Checks still running.</param>
/// <param name="Failing">Checks that failed, were cancelled or timed out.</param>
public sealed record ReviewCheckCounts(int Passing, int Pending, int Failing);
