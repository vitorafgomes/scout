namespace Scout.Plugin.GitHub.Abstractions;

/// <summary>
/// Starts and reports on the review of a pull request too large for one turn, run as a job in batches, in sentences the
/// model relays to the owner; the host provides it.
/// </summary>
public interface IPullRequestReviews
{
    /// <summary>Start, or resume, the review of pull request <paramref name="number"/> at <paramref name="headSha"/> and answer with its plan, or why it did not start.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="number">Pull request number.</param>
    /// <param name="headSha">The head commit the pull request's read answered.</param>
    /// <param name="files">Every changed file, each with its patch or null when GitHub sent none.</param>
    /// <param name="diffsLeftOut">How many diffs the pull request's answer left out to fit its budget.</param>
    /// <param name="notListed">How many changed files GitHub counts but did not list.</param>
    /// <param name="checks">The head commit's checks as counts; null when they could not be read.</param>
    /// <param name="ct">Cancels the start.</param>
    /// <param name="purpose">What the pull request says it does; null when it was not given.</param>
    /// <param name="again">True when the owner asked for the review again: a review done at the same head commit is then run once more.</param>
    /// <returns>A sentence for the model.</returns>
    Task<string> StartAsync(
        string owner, string repo, int number, string headSha, IReadOnlyList<ChangedFile> files, int diffsLeftOut, int notListed, PullRequestCheckCounts? checks, CancellationToken ct,
        PullRequestPurpose? purpose = null, bool again = false);

    /// <summary>How the latest review job of the pull request is going.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="number">Pull request number.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>A sentence for the model.</returns>
    Task<string> StatusAsync(string owner, string repo, int number, CancellationToken ct);
}

/// <summary>The checks of a commit as counts: passed (skipped and neutral included), still running, and failed.</summary>
/// <param name="Passing">Checks that passed, were skipped or neutral.</param>
/// <param name="Pending">Checks still running.</param>
/// <param name="Failing">Checks that failed, were cancelled or timed out.</param>
public sealed record PullRequestCheckCounts(int Passing, int Pending, int Failing);

/// <summary>What a pull request says it does: its title and description, and the issue its description names.</summary>
/// <param name="Title">The pull request's title.</param>
/// <param name="Description">The pull request's description, or null when empty.</param>
/// <param name="Issue">The number of the issue the description names, or null when it names none.</param>
/// <param name="IssueTitle">That issue's title, or null when it was not read.</param>
/// <param name="IssueBody">That issue's body, or null when empty or not read.</param>
/// <param name="IssueUnread">True when the description names an issue GitHub did not answer for.</param>
public sealed record PullRequestPurpose(string Title, string? Description, int? Issue, string? IssueTitle, string? IssueBody, bool IssueUnread);
