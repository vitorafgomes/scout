using Microsoft.Extensions.Options;
using Scout.Plugin.CodeReview.Abstractions;

namespace Scout.Plugin.CodeReview;

/// <summary>
/// Code-review plugin exposed to the LLM as Semantic Kernel functions. Each
/// method is a kernel function the model can invoke by name through
/// function-calling. The snippet functions run entirely on the configured model;
/// repository analysis additionally reads files from GitHub (read-only, GET only)
/// and is available only when a GitHub token is configured. Every call returns a
/// non-empty, user-safe string; model and transport failures are caught and
/// surfaced as a clean error line so a tool call never throws back into the
/// kernel.
/// </summary>
public sealed partial class CodeReviewPlugin
{
    private readonly ICodeReviewer _reviewer;
    private readonly IRepositoryFetcher? _fetcher;
    private readonly CodeReviewOptions _options;

    /// <summary>Creates the plugin over a reviewer, without repository analysis.</summary>
    /// <param name="reviewer">The model-backed reviewer. Must be non-null.</param>
    /// <param name="options">The plugin options; defaults are used when null.</param>
    public CodeReviewPlugin(ICodeReviewer reviewer, IOptions<CodeReviewOptions>? options = null)
        : this(reviewer, fetcher: null, options)
    {
    }

    /// <summary>
    /// Creates the plugin over a reviewer and an optional repository fetcher.
    /// A null fetcher disables repository analysis; snippet functions still work.
    /// </summary>
    /// <param name="reviewer">The model-backed reviewer. Must be non-null.</param>
    /// <param name="fetcher">The GitHub repository fetcher, or null when no token is configured.</param>
    /// <param name="options">The plugin options; defaults are used when null.</param>
    internal CodeReviewPlugin(
        ICodeReviewer reviewer,
        IRepositoryFetcher? fetcher,
        IOptions<CodeReviewOptions>? options = null)
    {
        _reviewer = reviewer ?? throw new ArgumentNullException(nameof(reviewer));
        _fetcher = fetcher;
        _options = options?.Value ?? new CodeReviewOptions();
    }

    private static bool IsHandledFailure(Exception ex)
        => ex is LlmReviewException
            or CodeReviewFetchException
            or HttpRequestException
            or OperationCanceledException;

    private static string ErrorLine(Exception ex) => ex switch
    {
        LlmReviewException review => review.Message,
        CodeReviewFetchException fetch => fetch.Message,
        OperationCanceledException => "The review timed out. Try again later.",
        _ => "Could not complete the review. Try again later.",
    };
}
