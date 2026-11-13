using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.SemanticKernel;
using Scout.Plugin.GitHub.Abstractions;

namespace Scout.Plugin.GitHub;

/// <summary>
/// <c>get_pull_request_checks</c>: what the pull request's own CI says at its head commit, the check runs and the commit
/// statuses in one list, read before a review is judged. Reads only.
/// </summary>
internal sealed partial class GitHubPlugin
{
    private static readonly JsonSerializerOptions ChecksJson = new(JsonSerializerDefaults.Web);

    /// <summary>Read the check runs and statuses of a pull request's head commit.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="number">Pull request number.</param>
    /// <param name="ct">Cancels the requests.</param>
    /// <returns>The head commit and its checks as JSON, or a clean message when not found or on failure.</returns>
    [KernelFunction("get_pull_request_checks")]
    [Description("Read what the pull request's CI says at its head commit: every check run and commit status with its name, status (queued, in_progress, completed), conclusion (success, failure, neutral, cancelled, skipped, timed_out, action_required, or null while running) and link. Call it before judging a pull request; pass the checks to present_review_findings as checks_json. Answers JSON: {\"sha\", \"checks\": [...]}, with a note when GitHub has more checks than one page shows.")]
    public async Task<string> GetPullRequestChecksAsync(
        [Description("Repository owner or organization login, e.g. octocat.")] string owner,
        [Description("Repository name, e.g. hello-world.")] string repo,
        [Description("Pull request number, e.g. 42.")] int number,
        CancellationToken ct)
    {
        if (!GitHubNames.IsValidRepository(owner, repo))
        {
            return GitHubNames.InvalidRepository;
        }

        if (number <= 0)
        {
            return "The pull request number must be positive.";
        }

        try
        {
            var head = await _client.GetPullRequestHeadAsync(owner, repo, number, ct).ConfigureAwait(false);
            if (head is null || head.Sha.Length == 0)
            {
                return $"Pull request #{number.ToString(CultureInfo.InvariantCulture)} not found in {owner}/{repo} or not accessible.";
            }

            var (checks, total) = await ChecksAtAsync(owner, repo, head.Sha, ct).ConfigureAwait(false);
            var note = total > checks.Count
                ? $"{checks.Count.ToString(CultureInfo.InvariantCulture)} of {total.ToString(CultureInfo.InvariantCulture)} checks shown"
                : null;
            return JsonSerializer.Serialize(
                new ChecksAnswer(head.Sha, checks.Select(c => new CheckDto(c.Name, c.Status, c.Conclusion, c.Url)).ToList(), note),
                ChecksJson);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>The check runs and the statuses at <paramref name="sha"/>, read together, and how many checks there are in all.</summary>
    private async Task<(IReadOnlyList<PullRequestCheck> Checks, int Total)> ChecksAtAsync(string owner, string repo, string sha, CancellationToken ct)
    {
        var runs = _client.ListCheckRunsAsync(owner, repo, sha, ct);
        var statuses = _client.ListStatusesAsync(owner, repo, sha, ct);
        await Task.WhenAll(runs, statuses).ConfigureAwait(false);
        var (checkRuns, total) = await runs.ConfigureAwait(false);
        var folded = await statuses.ConfigureAwait(false);
        return (checkRuns.Concat(folded).ToList(), total + folded.Count);
    }

    private sealed record ChecksAnswer(
        string Sha,
        IReadOnlyList<CheckDto> Checks,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note);

    private sealed record CheckDto(
        string Name,
        string Status,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Conclusion,
        string Url);
}
