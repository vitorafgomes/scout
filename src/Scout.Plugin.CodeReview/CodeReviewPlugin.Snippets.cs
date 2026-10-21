using System.ComponentModel;
using Microsoft.SemanticKernel;
using Scout.Plugin.CodeReview.Abstractions;

namespace Scout.Plugin.CodeReview;

/// <summary>
/// The snippet-only Code Review functions. Each runs a single model completion
/// over the supplied code with no GitHub access, and never throws: model and
/// transport failures are surfaced as a clean error line.
/// </summary>
public sealed partial class CodeReviewPlugin
{
    /// <summary>Assess the quality of a code snippet.</summary>
    /// <param name="code">The source code to review.</param>
    /// <param name="ct">Cancels the underlying model call.</param>
    /// <returns>A quality assessment, or a clean message on failure.</returns>
    [KernelFunction("analyze_code_quality")]
    [Description("Assess the quality of a code snippet: readability, naming, structure, error handling, and conventions.")]
    public Task<string> AnalyzeCodeQualityAsync(
        [Description("The source code to review.")] string code,
        CancellationToken ct)
        => ReviewSnippetAsync(ReviewKind.Quality, new CodeReviewInput(Snippet: code), ct);

    /// <summary>Detect security vulnerabilities in a code snippet.</summary>
    /// <param name="code">The source code to scan.</param>
    /// <param name="ct">Cancels the underlying model call.</param>
    /// <returns>Vulnerability findings, or a clean message on failure.</returns>
    [KernelFunction("detect_vulnerabilities")]
    [Description("Detect security vulnerabilities in a code snippet (injection, secrets, unsafe I/O, auth flaws) with severities and fixes.")]
    public Task<string> DetectVulnerabilitiesAsync(
        [Description("The source code to scan for vulnerabilities.")] string code,
        CancellationToken ct)
        => ReviewSnippetAsync(ReviewKind.Vulnerabilities, new CodeReviewInput(Snippet: code), ct);

    /// <summary>Suggest concrete improvements for a code snippet.</summary>
    /// <param name="code">The source code to improve.</param>
    /// <param name="ct">Cancels the underlying model call.</param>
    /// <returns>Improvement suggestions, or a clean message on failure.</returns>
    [KernelFunction("suggest_improvements")]
    [Description("Suggest concrete, actionable improvements for a code snippet: correctness, performance, clarity, and idioms.")]
    public Task<string> SuggestImprovementsAsync(
        [Description("The source code to improve.")] string code,
        CancellationToken ct)
        => ReviewSnippetAsync(ReviewKind.Improvements, new CodeReviewInput(Snippet: code), ct);

    /// <summary>Generate unit tests for a code snippet.</summary>
    /// <param name="code">The source code to test.</param>
    /// <param name="framework">Optional test framework hint, e.g. "xUnit".</param>
    /// <param name="ct">Cancels the underlying model call.</param>
    /// <returns>Generated test code, or a clean message on failure.</returns>
    [KernelFunction("generate_tests")]
    [Description("Generate focused unit tests for a code snippet, covering main paths and edge cases.")]
    public Task<string> GenerateTestsAsync(
        [Description("The source code to test.")] string code,
        [Description("Optional test framework, e.g. xUnit, pytest, Jest.")] string? framework,
        CancellationToken ct)
        => ReviewSnippetAsync(
            ReviewKind.Tests, new CodeReviewInput(Snippet: code, Framework: framework), ct);

    /// <summary>Explain what a code snippet does.</summary>
    /// <param name="code">The source code to explain.</param>
    /// <param name="level">Audience level, e.g. "beginner", "intermediate", "advanced".</param>
    /// <param name="ct">Cancels the underlying model call.</param>
    /// <returns>A plain-language explanation, or a clean message on failure.</returns>
    [KernelFunction("explain_code")]
    [Description("Explain what a code snippet does and how it works, at the requested audience level.")]
    public Task<string> ExplainCodeAsync(
        [Description("The source code to explain.")] string code,
        [Description("Audience level: beginner, intermediate, or advanced.")] string level = "intermediate",
        CancellationToken ct = default)
        => ReviewSnippetAsync(
            ReviewKind.Explain, new CodeReviewInput(Snippet: code, Level: level), ct);

    /// <summary>Review a pull request supplied as a unified diff.</summary>
    /// <param name="diff">The unified diff to review.</param>
    /// <param name="ct">Cancels the underlying model call.</param>
    /// <returns>A review ending in a verdict line, or a clean message on failure.</returns>
    [KernelFunction("review_pull_request")]
    [Description("Review a pull request supplied as a unified diff. Returns a review ending with 'VERDICT: approve' or 'VERDICT: request-changes'. Does not call GitHub.")]
    public Task<string> ReviewPullRequestAsync(
        [Description("The pull request as a unified diff (git diff text).")] string diff,
        CancellationToken ct)
        => ReviewSnippetAsync(ReviewKind.PullRequest, new CodeReviewInput(Diff: diff), ct);

    private async Task<string> ReviewSnippetAsync(
        ReviewKind kind, CodeReviewInput input, CancellationToken ct)
    {
        try
        {
            return await _reviewer.ReviewAsync(kind, input, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }
}
