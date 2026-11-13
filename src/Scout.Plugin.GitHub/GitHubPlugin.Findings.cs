using System.ComponentModel;
using System.Globalization;
using Microsoft.SemanticKernel;
using Scout.Llm.Abstractions;
using Scout.Plugin.GitHub.Abstractions;

namespace Scout.Plugin.GitHub;

/// <summary>
/// A review's or an audit's findings handed to Scout as data, so the reply ends with the block the desktop renders as cards
/// whatever the model writes around it. Nothing is written; a review reads the pull request's author, the signing identity
/// and the checks at the commit reviewed, and adds the last container run of its tests kept in memory.
/// </summary>
internal sealed partial class GitHubPlugin
{
    /// <summary>Characters of the findings block, well under the 32,000 a desktop reply carries with the review around it.</summary>
    internal const int MaxFindingsBlockLength = 24_000;

    internal const string FindingsTooLong =
        "The findings are over 24,000 characters as a block; send fewer findings, the ten that matter most, with shorter why and fix texts.";

    /// <summary>Record a code review's findings for the end of this turn's reply.</summary>
    /// <param name="repository">The repository as owner/repo.</param>
    /// <param name="number">Pull request number.</param>
    /// <param name="verdict">The one-line verdict.</param>
    /// <param name="findings_json">JSON array of findings: id, severity, title, path, line, context, why, fix, rule.</param>
    /// <param name="commit">The head commit reviewed; required, the card pins the verdict to it.</param>
    /// <param name="read">What was read, e.g. "6 of 6 files read".</param>
    /// <param name="ct">Cancels the reads of the pull request's author, the signing identity and the checks.</param>
    /// <returns>How many findings were recorded and what to write next, or the first problem with them.</returns>
    [KernelFunction("present_review_findings")]
    [Description("Record the findings of a code review so Scout shows them as cards the user decides on one by one; Scout adds the pull request's checks at the commit reviewed and the last container run of its tests itself. Call it once, before the final answer, with the same findings the answer lists, or [] when the review found nothing, so the user still sees what was checked and gives the verdict; a second call replaces the first. Never write a scout-findings block yourself. Nothing is posted to GitHub.")]
    public async Task<string> PresentReviewFindingsAsync(
        [Description("The repository as owner/repo, e.g. octocat/hello-world.")] string repository,
        [Description("Pull request number, e.g. 42.")] int number,
        [Description("The one-line verdict: ready to merge, ready after the blocking findings, or needs rework.")] string verdict,
        [Description("""JSON array of at most 50 findings, [] when the review found nothing, e.g. [{"id":"total","severity":"block","title":"Discounts applied after the sum","path":"src/orders/Checkout.cs","line":88,"context":"","why":"`total` is summed before the discount loop runs, so a discounted order is charged full price.","fix":"Apply the discounts before the sum."}]; severity is block, fix or nit; id is a short slug unique in the list; path and line are the file and the line after the change as the diff shows it; context is empty or where the finding comes from; rule is optional, the project rule, ADR or convention the finding departs from, 1 to 300 characters.""")] string findings_json,
        [Description("The head commit reviewed, the SHA get_pull_request answered, e.g. ec7f951; the verdict on the card is pinned to it.")] string? commit = null,
        [Description("What was read, e.g. 6 of 6 files read.")] string? read = null,
        CancellationToken ct = default)
    {
        var (findings, problem) = ReviewFindings.Parse(repository, number, commit, verdict, read, findings_json);
        if (findings is null)
        {
            return problem!;
        }

        var parts = findings.Repository.Split('/');
        return Present((await WithEvidenceAsync(findings, parts[0], parts[1], number, ct).ConfigureAwait(false), null));
    }

    /// <summary>
    /// The findings with what Scout reads itself: the checks at the commit reviewed while it is still the pull request's head,
    /// the last container run kept for that commit, and whether the identity that would sign a verdict opened the pull
    /// request, so the card offers no approval GitHub would refuse. What cannot be read is left out.
    /// </summary>
    private async Task<ReviewFindings> WithEvidenceAsync(ReviewFindings findings, string owner, string repo, int number, CancellationToken ct)
    {
        var tests = _testRuns?.Find(owner, repo, findings.Commit);
        try
        {
            var signer = _signing.SignerFor(GitHubWriteKind.Verdict, owner, repo);
            var headRead = _client.GetPullRequestHeadAsync(owner, repo, number, ct);
            Task<string?> loginRead = signer.ByBot ? Task.FromResult(signer.Login) : LoginAsync(ct);
            await Task.WhenAll(headRead, loginRead).ConfigureAwait(false);
            var head = await headRead.ConfigureAwait(false);
            var login = await loginRead.ConfigureAwait(false);
            if (head is null)
            {
                return findings with { Tests = tests };
            }

            var selfAuthored = login is not null && string.Equals(head.AuthorLogin, login, StringComparison.OrdinalIgnoreCase);
            IReadOnlyList<ReviewCheck>? checks = null;
            if (findings.Commit is { } commit && head.Sha.StartsWith(commit, StringComparison.OrdinalIgnoreCase))
            {
                var (read, _) = await ChecksAtAsync(owner, repo, head.Sha, ct).ConfigureAwait(false);
                checks = read.Count == 0 ? null : read.Select(c => new ReviewCheck(c.Name, c.Status, c.Conclusion, c.Url)).ToList();
            }

            return findings with { Checks = checks, Tests = tests, SelfAuthored = selfAuthored ? true : null };
        }
        catch (Exception ex) when (IsHandledFailure(ex) && !ct.IsCancellationRequested)
        {
            return findings with { Tests = tests };
        }
    }

    private async Task<string?> LoginAsync(CancellationToken ct) => await _client.GetSignedInLoginAsync(ct).ConfigureAwait(false);

    /// <summary>Record a whole-project audit's findings for the end of this turn's reply.</summary>
    /// <param name="repository">The repository as owner/repo.</param>
    /// <param name="verdict">The one-line verdict.</param>
    /// <param name="findings_json">JSON array of findings: id, severity, title, path, line, context, why, fix, rule.</param>
    /// <param name="commit">The commit audited, when known.</param>
    /// <param name="read">What was read, e.g. "the whole repository".</param>
    /// <param name="known_json">JSON array of the limitations the project already documents.</param>
    /// <returns>How many findings were recorded and what to write next, or the first problem with them.</returns>
    [KernelFunction("present_audit_findings")]
    [Description("Record the findings of a whole-project audit so Scout shows them as cards the user decides on one by one, each one an issue the user may open. Call it once, before the final answer, with every finding the audit reports; a second call replaces the first. Never write a scout-findings block yourself. Nothing is posted to GitHub.")]
    public static string PresentAuditFindings(
        [Description("The repository as owner/repo, e.g. octocat/hello-world.")] string repository,
        [Description("The one-line verdict: sound, sound after the High findings, or needs rework.")] string verdict,
        [Description("""JSON array of 1 to 50 findings, e.g. [{"id":"swallow","severity":"block","title":"Gateway errors read as no orders","path":"src/orders/Gateway.cs","line":12,"context":"","why":"The catch returns an empty list, so an outage reads as no orders.","fix":"Let it fail, or return a result that says the call failed.","rule":"Defect"}]; severity is block for High, fix for Medium, nit for Low; id is a short slug unique in the list; path and line are the file and the line; context is empty or where the finding comes from; rule is the project rule, ADR, convention or requirement the finding departs from, or the word Defect, 1 to 300 characters.""")] string findings_json,
        [Description("The commit audited, e.g. ec7f951; empty when unknown.")] string? commit = null,
        [Description("What was read, e.g. the whole repository, or src/orders and its tests.")] string? read = null,
        [Description("""JSON array of at most 20 limitations the project already documents, each 1 to 300 characters, e.g. ["No paging on the orders list, noted in the README."]; shown as a note under the cards, never as findings; empty when none.""")] string? known_json = null) =>
        Present(ReviewFindings.ParseAudit(repository, commit, verdict, read, findings_json, known_json));

    private static string Present((ReviewFindings? Findings, string? Problem) parsed)
    {
        var (findings, problem) = parsed;
        if (findings is null)
        {
            return problem!;
        }

        var block = findings.ToBlock();
        if (block.Length > MaxFindingsBlockLength)
        {
            return FindingsTooLong;
        }

        TurnAttachments.Attach(block);
        return $"Findings recorded: {findings.Findings.Count.ToString(CultureInfo.InvariantCulture)}. End your answer with the verdict; the cards show the findings.";
    }
}
