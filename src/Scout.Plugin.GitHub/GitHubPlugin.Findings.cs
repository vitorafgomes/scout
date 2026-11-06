using System.ComponentModel;
using System.Globalization;
using Microsoft.SemanticKernel;
using Scout.Llm.Abstractions;

namespace Scout.Plugin.GitHub;

/// <summary>
/// The review's findings handed to Scout as data, so the reply ends with the block the desktop renders as cards
/// whatever the model writes around it. Nothing leaves the process and nothing is written.
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
    /// <param name="findings_json">JSON array of findings: id, severity, title, path, line, context, why, fix.</param>
    /// <param name="commit">The head commit reviewed, when known.</param>
    /// <param name="read">What was read, e.g. "6 of 6 files read".</param>
    /// <returns>How many findings were recorded and what to write next, or the first problem with them.</returns>
    [KernelFunction("present_review_findings")]
    [Description("Record the findings of a code review so Scout shows them as cards the user decides on one by one. Call it once, before the final answer, with the same findings the answer lists; a second call replaces the first. Never write a scout-findings block yourself. Nothing is posted to GitHub.")]
    public static string PresentReviewFindings(
        [Description("The repository as owner/repo, e.g. octocat/hello-world.")] string repository,
        [Description("Pull request number, e.g. 42.")] int number,
        [Description("The one-line verdict: ready to merge, ready after the blocking findings, or needs rework.")] string verdict,
        [Description("""JSON array of 1 to 50 findings, e.g. [{"id":"total","severity":"block","title":"Discounts applied after the sum","path":"src/orders/Checkout.cs","line":88,"context":"","why":"`total` is summed before the discount loop runs, so a discounted order is charged full price.","fix":"Apply the discounts before the sum."}]; severity is block, fix or nit; id is a short slug unique in the list; path and line are the file and the line after the change as the diff shows it; context is empty or where the finding comes from.""")] string findings_json,
        [Description("The head commit reviewed, e.g. ec7f951; empty when unknown.")] string? commit = null,
        [Description("What was read, e.g. 6 of 6 files read.")] string? read = null)
    {
        var (findings, problem) = ReviewFindings.Parse(repository, number, commit, verdict, read, findings_json);
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
