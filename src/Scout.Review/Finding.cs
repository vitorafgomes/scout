namespace Scout.Review;

/// <summary>One finding of a review or an audit, anchored to a line of a file of the repository.</summary>
/// <param name="Id">The finding's id, unique among the findings of a card.</param>
/// <param name="Severity">One of <see cref="Severities.All"/>.</param>
/// <param name="Title">What the finding names.</param>
/// <param name="Path">The file, relative to the repository root.</param>
/// <param name="Line">The line in the file, from 1.</param>
/// <param name="Context">Why it matters, or null.</param>
/// <param name="Why">What is wrong.</param>
/// <param name="Fix">The proposed fix.</param>
/// <param name="Rule">The project rule or document it departs from, or null.</param>
/// <param name="Evidence">
/// An exact quote of the new side of the finding's file's patch, or of its removed lines without the leading '-', from which a review derives its line
/// (<see cref="DiffQuote"/>), or null: an audit, and a finding on a deleted file, carry none.
/// </param>
public sealed record Finding(string Id, string Severity, string Title, string Path, int Line, string? Context, string Why, string Fix, string? Rule, string? Evidence = null);

/// <summary>The severities of a finding, and what blocking means.</summary>
public static class Severities
{
    /// <summary>A finding the pull request should not be merged with.</summary>
    public const string Block = "block";

    /// <summary>A finding to fix, not blocking.</summary>
    public const string Fix = "fix";

    /// <summary>An optional suggestion.</summary>
    public const string Nit = "nit";

    /// <summary>Every severity, the blocking one first.</summary>
    public static IReadOnlyList<string> All { get; } = [Block, Fix, Nit];

    /// <summary>Whether <paramref name="severity"/> is one of <see cref="All"/>.</summary>
    public static bool IsKnown(string? severity) => severity is Block or Fix or Nit;

    /// <summary>Whether a finding of <paramref name="severity"/> blocks: only <see cref="Block"/> does.</summary>
    public static bool IsBlocking(string? severity) => severity == Block;
}
