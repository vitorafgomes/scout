using System.Globalization;

namespace Scout.Review;

/// <summary>One kept finding as the offer weighs it: its severity and whether Scout's second reading could not confirm it.</summary>
public sealed record OfferFinding(string Severity, bool Unconfirmed);

/// <summary>The checks of the pull request's head commit: how many passed, failed and are still running, and whether Scout read every one.</summary>
public sealed record OfferChecks(int Passed, int Failed, int Running, bool Complete);

/// <summary>What the offer of a review is decided from.</summary>
/// <param name="Findings">The kept findings.</param>
/// <param name="FilesRead">The changed files the review read.</param>
/// <param name="FilesChanged">The files the pull request changes.</param>
/// <param name="Tests">The run of the pull request's tests; null when none is known.</param>
/// <param name="Checks">The head commit's checks; null when they could not be read.</param>
/// <param name="ReviewerIsAuthor">True when the identity that would approve opened the pull request.</param>
/// <param name="Mergeable">False when the pull request conflicts with its base branch; null when GitHub has not said.</param>
/// <param name="HeadMoved">True when the pull request moved past the commit reviewed.</param>
/// <param name="ReadCountKnown">False when the files read and changed are not known as numbers, so the offer never approves.</param>
/// <param name="EarlierOpen">The severities of Scout's findings from earlier reviews that are still open at this head; a fixed, answered or undecided one is not here.</param>
public sealed record OfferInput(
    IReadOnlyList<OfferFinding> Findings, int FilesRead, int FilesChanged, TestRunFacts? Tests, OfferChecks? Checks,
    bool ReviewerIsAuthor = false, bool? Mergeable = true, bool HeadMoved = false, bool ReadCountKnown = true, IReadOnlyList<string>? EarlierOpen = null);

/// <summary>The verdict a review offers, why in one sentence, and whether sending its findings as a comment instead is allowed.</summary>
/// <param name="Verdict">One of <see cref="ReviewOffers.Approve"/>, <see cref="ReviewOffers.RequestChanges"/>, <see cref="ReviewOffers.Comment"/>, <see cref="ReviewOffers.Wait"/> or <see cref="ReviewOffers.None"/>.</param>
/// <param name="Reason">One sentence that says why.</param>
/// <param name="CommentInstead">True exactly when the offer is <see cref="ReviewOffers.RequestChanges"/>.</param>
public sealed record Offer(string Verdict, string Reason, bool CommentInstead);

/// <summary>
/// The one decision of what a review offers the owner. A confirmed finding that needs fixing (block or fix), tests that
/// really failed, or a check that failed: request changes. Otherwise anything that keeps the pull request from being shown
/// clean (optional suggestions, unconfirmed findings, files not read, tests that could not run and are not covered by the
/// pull request's own checks all passing, checks not read, and what refuses an approval) offers a comment, never an
/// approval, and says which. A clean review, its checks passed or still running, offers a comment too: it says what the
/// review found and that Scout does not approve on its own, so a person approves on GitHub. A pull request that moved
/// past the commit reviewed offers nothing.
/// </summary>
public static class ReviewOffers
{
    /// <summary>Approve the pull request: reserved, not given by <see cref="Decide"/> today; the card still draws it when given one, and the approval returns under the owner's flag.</summary>
    public const string Approve = "approve";

    /// <summary>Request changes on it.</summary>
    public const string RequestChanges = "request_changes";

    /// <summary>Post the findings as a review that only comments.</summary>
    public const string Comment = "comment";

    /// <summary>Approve when the checks still running pass: reserved, not given by <see cref="Decide"/> today; the card still draws it when given one.</summary>
    public const string Wait = "wait";

    /// <summary>Nothing: the review no longer matches the pull request.</summary>
    public const string None = "none";

    /// <summary>The reason of an offer for a pull request that moved past the commit reviewed.</summary>
    public const string Moved = "The pull request changed since the review; review it again.";

    /// <summary>What <paramref name="input"/> offers.</summary>
    public static Offer Decide(OfferInput input)
    {
        if (input.HeadMoved)
        {
            return new(None, Moved, false);
        }

        var needFixing = input.Findings.Count(f => !f.Unconfirmed && (f.Severity is Severities.Block or Severities.Fix));
        var earlierOpen = input.EarlierOpen?.Count(s => s is Severities.Block or Severities.Fix) ?? 0;
        var testsFailed = input.Tests is { Ran: true, ExitCode: not 0 } run
            && !(run.Projects.Any(p => p.CouldNotRun) && !run.Projects.Any(p => p.Failed > 0 && !p.CouldNotRun));
        var checksFailed = input.Checks?.Failed ?? 0;
        if (needFixing > 0 || earlierOpen > 0 || testsFailed || checksFailed > 0)
        {
            List<string> why = [];
            if (needFixing > 0)
            {
                why.Add($"{Count(needFixing, "finding")} {(needFixing == 1 ? "needs" : "need")} fixing");
            }

            if (earlierOpen > 0)
            {
                why.Add($"{Count(earlierOpen, "earlier finding")} {(earlierOpen == 1 ? "is" : "are")} still open");
            }

            if (testsFailed)
            {
                why.Add("the pull request's tests failed");
            }

            if (checksFailed > 0)
            {
                why.Add($"{Count(checksFailed, "check")} failed");
            }

            return new(RequestChanges, $"Request changes: {Join(why)}.", true);
        }

        List<string> comment = [];
        var nits = input.Findings.Count(f => !f.Unconfirmed);
        var unconfirmed = input.Findings.Count(f => f.Unconfirmed);
        if (nits > 0)
        {
            comment.Add($"{Count(nits, "optional suggestion")} {(nits == 1 ? "was" : "were")} kept");
        }

        if (unconfirmed > 0)
        {
            comment.Add($"{Count(unconfirmed, "finding")} could not be confirmed");
        }

        if (!input.ReadCountKnown)
        {
            comment.Add("how many changed files were read is not known");
        }
        else if (input.FilesRead < input.FilesChanged)
        {
            comment.Add(string.Create(CultureInfo.InvariantCulture, $"{input.FilesChanged - input.FilesRead} of {input.FilesChanged} changed files were not read"));
        }

        var checks = input.Checks;
        var checksClean = checks is { Complete: true, Failed: 0 };
        var testsPassed = input.Tests is { Ran: true, ExitCode: 0 };
        if (checks is null)
        {
            comment.Add("the pull request's checks could not be read");
        }
        else if (!checks.Complete)
        {
            comment.Add("the commit has more checks than Scout could read");
        }

        if (!testsPassed)
        {
            var notRun = input.Tests is { Ran: true } ? "some tests could not run here" : "no test ran here";
            if (!checksClean)
            {
                comment.Add($"{notRun} and the pull request's checks cannot cover them");
            }
            else if (checks!.Running == 0 && checks.Passed == 0)
            {
                comment.Add($"{notRun} and no check of the pull request runs them");
            }
        }

        if (input.ReviewerIsAuthor)
        {
            comment.Add("GitHub does not let the author approve their own pull request");
        }

        if (input.Mergeable == false)
        {
            comment.Add("the pull request has conflicts with its base branch");
        }
        else if (input.Mergeable is null)
        {
            comment.Add("GitHub has not said whether the pull request can be merged");
        }

        if (comment.Count > 0)
        {
            return new(Comment, $"Comment, not approve: {Join(comment)}.", false);
        }

        var found = checks!.Running > 0
            ? string.Create(CultureInfo.InvariantCulture, $"every changed file was read and nothing needs fixing, and {Count(checks.Running, "check")} {(checks.Running == 1 ? "is" : "are")} still running")
            : testsPassed
                ? "every changed file was read, nothing needs fixing, the tests passed and every check passed"
                : input.Tests is { Ran: true }
                    ? "every changed file was read, nothing needs fixing, and every check passed, which covers the tests that could not run here"
                    : "every changed file was read, nothing needs fixing, and every check passed, which covers the tests, since no test ran here";
        return new(Comment, $"Comment, not approve: {found}. {NotOnItsOwn}", false);
    }

    /// <summary>Said of every review the decision finds clean: Scout gives no approval on its own.</summary>
    public const string NotOnItsOwn = "Scout does not approve on its own, so a person approves the pull request on GitHub.";

    /// <summary>The files read and changed a read line gives, such as <c>6 of 7 files read</c>; null when it gives no such numbers.</summary>
    public static (int Read, int Of)? ReadCount(string? read)
    {
        var match = System.Text.RegularExpressions.Regex.Match(read ?? string.Empty, @"\A\s*(\d{1,6}) of (\d{1,6}) files? read\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return match.Success && int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) is var r && int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) is var of && r <= of
            ? (r, of)
            : null;
    }

    private static string Count(int count, string noun) => string.Create(CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? string.Empty : "s")}");

    private static string Join(List<string> parts) => parts.Count == 1 ? parts[0] : $"{string.Join(", ", parts.Take(parts.Count - 1))} and {parts[^1]}";
}
