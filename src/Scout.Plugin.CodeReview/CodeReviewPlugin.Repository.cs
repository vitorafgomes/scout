using System.ComponentModel;
using Microsoft.SemanticKernel;
using Scout.Plugin.CodeReview.Abstractions;

namespace Scout.Plugin.CodeReview;

/// <summary>
/// The repository-level Code Review function. Reads a bounded selection of key
/// files from GitHub (read-only, GET only) and asks the model to review them.
/// Available only when a GitHub token is configured; never throws.
/// </summary>
public sealed partial class CodeReviewPlugin
{
    private const string TokenRequiredMessage =
        "Repository analysis requires a configured GitHub token. The snippet review functions still work without one.";

    /// <summary>Analyze a GitHub repository by reviewing a selection of its key files.</summary>
    /// <param name="repositoryUrl">
    /// Repository URL, e.g. https://github.com/octocat/Hello-World. When omitted,
    /// the configured default repository URL is used.
    /// </param>
    /// <param name="ct">Cancels the underlying GitHub and model calls.</param>
    /// <returns>A repository-level review, or a clean message on failure.</returns>
    [KernelFunction("analyze_repository")]
    [Description("Analyze a GitHub repository: read a selection of its key files and review the project's purpose, architecture, and risks. Requires a configured GitHub token.")]
    public async Task<string> AnalyzeRepositoryAsync(
        [Description("Repository URL, e.g. https://github.com/octocat/Hello-World. Optional; falls back to the configured default.")] string? repositoryUrl = null,
        CancellationToken ct = default)
    {
        var url = string.IsNullOrWhiteSpace(repositoryUrl) ? _options.DefaultRepoUrl : repositoryUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return "No repository URL was provided and no default is configured. Pass a GitHub repository URL.";
        }

        if (!RepositoryUrlParser.TryParse(url, out var coords) || coords is null)
        {
            return "That does not look like a GitHub repository URL. Use https://github.com/owner/repo.";
        }

        if (_fetcher is null)
        {
            return TokenRequiredMessage;
        }

        try
        {
            var snapshot = await _fetcher.FetchAsync(coords, ct).ConfigureAwait(false);

            // Nothing reviewable (empty repo, or every key file binary/oversize):
            // short-circuit rather than asking the model to review an empty corpus.
            if (snapshot.Files.Count == 0)
            {
                return $"No reviewable text files were found in {snapshot.FullName}.";
            }

            var input = new CodeReviewInput(
                Files: snapshot.Files
                    .Select(f => new ReviewFile(f.Path, f.Content))
                    .ToList(),
                RepositoryName: snapshot.FullName);

            return await _reviewer.ReviewAsync(ReviewKind.Repository, input, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }
}
