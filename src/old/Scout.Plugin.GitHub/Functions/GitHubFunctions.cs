using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Octokit;
using Scout.Core.Features;
using Scout.Plugin.GitHub.Contracts;
using Scout.Plugin.GitHub.Internal;

namespace Scout.Plugin.GitHub.Functions;

/// <summary>
/// Semantic Kernel functions exposed by <see cref="GitHubPlugin"/>. Every
/// function is read-only, except <see cref="CreateIssueAsync"/> which is
/// the single descriptive write permitted by Scout's plugin invariant
/// (see ADR <c>2026-05-23-descriptive-vs-executive-writes</c>).
/// </summary>
public sealed class GitHubFunctions
{
    internal const string ErrorCredentialsInvalid = "credentials-invalid";
    internal const string ErrorNotFound = "not-found";
    internal const string ErrorRateLimited = "rate-limited";
    internal const string ErrorMissingScopes = "missing-scopes";
    internal const string ErrorGitHubApi = "github-api-error";
    internal const string ErrorMissingRepo = "missing-repo";

    private const string DefaultRepoSettingKey = "default_repo_url";

    private readonly IGitHubClientFactory _factory;
    private readonly ILogger<GitHubFunctions> _logger;
    private readonly IOptions<GitHubPluginOptions> _options;
    private readonly IFeatureSettingsReader _settings;

    /// <summary>Construct the function surface.</summary>
    public GitHubFunctions(
        IGitHubClientFactory factory,
        ILogger<GitHubFunctions> logger,
        IOptions<GitHubPluginOptions> options,
        IFeatureSettingsReader settings)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>List repositories visible to the authenticated user.</summary>
    [KernelFunction("list_repositories")]
    [Description("Read-only: list repositories visible to the authenticated GitHub user, optionally scoped to a specific user or organisation owner.")]
    public Task<GitHubResult<IReadOnlyList<GitHubRepository>>> ListRepositoriesAsync(
        [Description("Owner login (user or org). Use empty string for the authenticated user's own repositories.")]
        string owner,
        [Description("Filter type passed to GitHub: 'user' or 'org'. Default null treats owner as a user.")]
        string? type = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        return InvokeAsync<IReadOnlyList<GitHubRepository>>(
            async client =>
            {
                IReadOnlyList<Repository> repos;
                if (string.IsNullOrWhiteSpace(owner))
                {
                    repos = await client.Repository.GetAllForCurrent().ConfigureAwait(false);
                }
                else if (string.Equals(type, "org", StringComparison.OrdinalIgnoreCase))
                {
                    repos = await client.Repository.GetAllForOrg(owner).ConfigureAwait(false);
                }
                else
                {
                    repos = await client.Repository.GetAllForUser(owner).ConfigureAwait(false);
                }

                var mapped = new List<GitHubRepository>(repos.Count);
                foreach (var r in repos)
                {
                    mapped.Add(GitHubMappers.Map(r));
                }
                return (IReadOnlyList<GitHubRepository>)mapped;
            },
            cancellationToken);
    }

    /// <summary>List pull requests on a repository.</summary>
    [KernelFunction("list_pull_requests")]
    [Description("Read-only: list pull requests on a repository filtered by state ('open', 'closed', 'all').")]
    public Task<GitHubResult<IReadOnlyList<GitHubPullRequestSummary>>> ListPullRequestsAsync(
        [Description("Repository owner login.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("PR state filter: 'open', 'closed' or 'all'. Defaults to 'open'.")]
        string? state = "open",
        CancellationToken cancellationToken = default)
    {
        return InvokeWithRepoAsync<IReadOnlyList<GitHubPullRequestSummary>>(
            owner,
            repo,
            async (client, o, r) =>
            {
                var request = new PullRequestRequest { State = ParseItemStateFilter(state) };
                var prs = await client.PullRequest
                    .GetAllForRepository(o, r, request)
                    .ConfigureAwait(false);

                var mapped = new List<GitHubPullRequestSummary>(prs.Count);
                foreach (var pr in prs)
                {
                    mapped.Add(GitHubMappers.MapSummary(pr));
                }
                return (IReadOnlyList<GitHubPullRequestSummary>)mapped;
            },
            cancellationToken);
    }

    private async Task<GitHubResult<T>> InvokeWithRepoAsync<T>(
        string? owner,
        string? repo,
        Func<IGitHubClient, string, string, Task<T>> call,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveOwnerRepoAsync(owner, repo, cancellationToken)
            .ConfigureAwait(false);
        if (resolved is not (string o, string r))
        {
            return GitHubResult<T>.Fail(
                ErrorMissingRepo,
                "No owner/repo supplied and no usable 'default_repo_url' setting is configured for the GitHub feature.");
        }

        return await InvokeAsync<T>(client => call(client, o, r), cancellationToken)
            .ConfigureAwait(false);
    }

    // When owner/repo arrive blank, fall back to the GitHub feature's
    // 'default_repo_url' setting and parse it. Returns null when neither an
    // explicit pair nor a parseable default is available.
    private async Task<(string Owner, string Repo)?> ResolveOwnerRepoAsync(
        string? owner,
        string? repo,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(repo))
        {
            return (owner.Trim(), repo.Trim());
        }

        var defaultUrl = await _settings
            .GetAsync(GitHubPlugin.FeatureIdValue, DefaultRepoSettingKey, cancellationToken)
            .ConfigureAwait(false);

        return GitHubRepoUrlParser.TryParse(defaultUrl, out var o, out var r)
            ? (o, r)
            : null;
    }

    /// <summary>Get detailed information about a single pull request, including changed files.</summary>
    [KernelFunction("get_pull_request_details")]
    [Description("Read-only: fetch full details for one pull request including changed file list, additions and deletions.")]
    public Task<GitHubResult<GitHubPullRequestDetails>> GetPullRequestDetailsAsync(
        [Description("Repository owner login.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Pull request number (positive integer).")] int number,
        CancellationToken cancellationToken = default)
    {
        if (number <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(number), "Pull request number must be positive.");
        }

        return InvokeWithRepoAsync<GitHubPullRequestDetails>(
            owner,
            repo,
            async (client, o, r) =>
            {
                var pr = await client.PullRequest.Get(o, r, number).ConfigureAwait(false);
                var files = await client.PullRequest.Files(o, r, number).ConfigureAwait(false);

                var mappedFiles = new List<GitHubChangedFile>(files.Count);
                foreach (var f in files)
                {
                    mappedFiles.Add(GitHubMappers.Map(f));
                }

                return GitHubMappers.MapDetails(pr, mappedFiles);
            },
            cancellationToken);
    }

    /// <summary>List issues on a repository, excluding pull requests.</summary>
    [KernelFunction("list_issues")]
    [Description("Read-only: list issues on a repository (pull requests excluded) filtered by state ('open', 'closed', 'all').")]
    public Task<GitHubResult<IReadOnlyList<GitHubIssueSummary>>> ListIssuesAsync(
        [Description("Repository owner login.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Issue state filter: 'open', 'closed' or 'all'. Defaults to 'open'.")]
        string? state = "open",
        CancellationToken cancellationToken = default)
    {
        return InvokeWithRepoAsync<IReadOnlyList<GitHubIssueSummary>>(
            owner,
            repo,
            async (client, o, r) =>
            {
                var request = new RepositoryIssueRequest { State = ParseItemStateFilter(state) };
                var issues = await client.Issue
                    .GetAllForRepository(o, r, request)
                    .ConfigureAwait(false);

                var mapped = new List<GitHubIssueSummary>(issues.Count);
                foreach (var issue in issues)
                {
                    if (!GitHubMappers.IsRealIssue(issue))
                    {
                        continue;
                    }
                    mapped.Add(GitHubMappers.MapSummary(issue));
                }
                return (IReadOnlyList<GitHubIssueSummary>)mapped;
            },
            cancellationToken);
    }

    /// <summary>Get detailed information about a single issue.</summary>
    [KernelFunction("get_issue")]
    [Description("Read-only: fetch full details for one issue including labels, assignees and body.")]
    public Task<GitHubResult<GitHubIssueDetails>> GetIssueAsync(
        [Description("Repository owner login.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Issue number (positive integer).")] int number,
        CancellationToken cancellationToken = default)
    {
        if (number <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(number), "Issue number must be positive.");
        }

        return InvokeWithRepoAsync<GitHubIssueDetails>(
            owner,
            repo,
            async (client, o, r) =>
            {
                var issue = await client.Issue.Get(o, r, number).ConfigureAwait(false);
                return GitHubMappers.MapDetails(issue);
            },
            cancellationToken);
    }

    /// <summary>
    /// Create a new issue. This is the single descriptive write the
    /// plugin exposes (per the read-only invariant ADR).
    /// </summary>
    [KernelFunction("create_issue")]
    [Description("Descriptive write: create a new issue on the repository. Title and body are NOT logged.")]
    public Task<GitHubResult<GitHubCreatedIssue>> CreateIssueAsync(
        [Description("Repository owner login.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Issue title (non-empty).")] string title,
        [Description("Issue body markdown (may be empty).")] string body,
        [Description("Optional labels to attach at creation time.")]
        IReadOnlyList<string>? labels = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(body);

        return InvokeWithRepoAsync<GitHubCreatedIssue>(
            owner,
            repo,
            async (client, o, r) =>
            {
                var newIssue = new NewIssue(title)
                {
                    Body = body,
                };
                if (labels is { Count: > 0 })
                {
                    foreach (var label in labels)
                    {
                        if (!string.IsNullOrWhiteSpace(label))
                        {
                            newIssue.Labels.Add(label);
                        }
                    }
                }

                var created = await client.Issue.Create(o, r, newIssue).ConfigureAwait(false);

                // Audit log: never include the title text or body. The
                // SHA256 prefix lets us correlate without leaking content.
                _logger.LogInformation(
                    "github.create_issue: owner={Owner} repo={Repo} number={Number} titleSha256First8={TitleSha256First8}",
                    o,
                    r,
                    created.Number,
                    Sha256First8(title));

                return new GitHubCreatedIssue(created.Number, created.HtmlUrl ?? string.Empty);
            },
            cancellationToken);
    }

    private async Task<GitHubResult<T>> InvokeAsync<T>(
        Func<IGitHubClient, Task<T>> call,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IGitHubClient client;
        try
        {
            client = await _factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return GitHubResult<T>.Fail(ErrorCredentialsInvalid, ex.Message);
        }

        try
        {
            var value = await call(client).ConfigureAwait(false);
            WarnIfNearRateLimit(client);
            return GitHubResult<T>.Ok(value);
        }
        catch (RateLimitExceededException ex)
        {
            return GitHubResult<T>.Fail(
                ErrorRateLimited,
                string.Format(CultureInfo.InvariantCulture, "Rate limit hit; resets at {0:O}.", ex.Reset),
                ex.Reset);
        }
        catch (AuthorizationException ex)
        {
            return GitHubResult<T>.Fail(ErrorCredentialsInvalid, ex.Message);
        }
        catch (NotFoundException ex)
        {
            return GitHubResult<T>.Fail(ErrorNotFound, ex.Message);
        }
        catch (ForbiddenException ex)
        {
            // RateLimit derives from Forbidden, so we only get here on
            // non-rate-limit 403s (typically missing-scope problems).
            return GitHubResult<T>.Fail(ErrorMissingScopes, ex.Message);
        }
        catch (ApiException ex) when (
            ex.StatusCode == HttpStatusCode.ServiceUnavailable
            || ex.StatusCode == HttpStatusCode.GatewayTimeout
            || ex.StatusCode == HttpStatusCode.BadGateway)
        {
            return GitHubResult<T>.Fail(ErrorGitHubApi, ex.Message);
        }
        catch (ApiException ex)
        {
            return GitHubResult<T>.Fail(ErrorGitHubApi, ex.Message);
        }
    }

    private void WarnIfNearRateLimit(IGitHubClient client)
    {
        var info = client.GetLastApiInfo();
        var remaining = info?.RateLimit?.Remaining;
        if (remaining is int r && r < _options.Value.RateLimitWarningThreshold)
        {
            _logger.LogWarning(
                "GitHub rate limit low: remaining={Remaining} resets={Reset:O}",
                r,
                info!.RateLimit!.Reset);
        }
    }

    private static ItemStateFilter ParseItemStateFilter(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return ItemStateFilter.Open;
        }

        return state.Trim().ToLowerInvariant() switch
        {
            "open" => ItemStateFilter.Open,
            "closed" => ItemStateFilter.Closed,
            "all" => ItemStateFilter.All,
            _ => ItemStateFilter.Open,
        };
    }

    internal static string Sha256First8(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).Substring(0, 8);
    }
}
