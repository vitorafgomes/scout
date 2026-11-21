using System.Globalization;
using System.Text.RegularExpressions;

namespace Scout.Review;

/// <summary>
/// The one validation of a finding and of a card's findings: the first problem in words, or null when there is none.
/// </summary>
public static partial class FindingValidation
{
    private const string Allowed = "are allowed.";

    [GeneratedRegex(@"\A[A-Za-z0-9-]+\z")]
    private static partial Regex FindingId();

    /// <summary>
    /// The first problem of <paramref name="findings"/>: more than <see cref="FindingLimits.MaxFindings"/>, a missing or
    /// invalid finding, or an id two findings share, each named by its position from 1; null when there is none.
    /// </summary>
    public static string? ProblemOf(IReadOnlyList<Finding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        if (findings.Count > FindingLimits.MaxFindings)
        {
            return $"There are {Number(findings.Count)} findings; at most {Number(FindingLimits.MaxFindings)} {Allowed}";
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < findings.Count; index++)
        {
            var position = Number(index + 1);
            if (findings[index] is not { } finding)
            {
                return $"Finding {position} is missing.";
            }

            if (ProblemOf(finding) is { } problem)
            {
                return $"Finding {position} {problem}";
            }

            if (!ids.Add(finding.Id))
            {
                return $"Finding {position} repeats the id {finding.Id}; each id must be unique.";
            }
        }

        return null;
    }

    /// <summary>
    /// The first problem of <paramref name="finding"/>, as the end of a sentence that starts with the finding's name; null
    /// when there is none. A finding needs an id, a severity, a title, a path, a line, a why and a fix, each within
    /// <see cref="FindingLimits"/>.
    /// </summary>
    public static string? ProblemOf(Finding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        if (finding.Id is null || finding.Id.Length > FindingLimits.MaxIdLength || !FindingId().IsMatch(finding.Id))
        {
            return $"needs an id of 1 to {Number(FindingLimits.MaxIdLength)} letters, digits or hyphens.";
        }

        if (!Severities.IsKnown(finding.Severity))
        {
            return "needs a severity of block, fix or nit.";
        }

        if (string.IsNullOrWhiteSpace(finding.Title) || finding.Title.Length > FindingLimits.MaxTitleLength)
        {
            return $"needs a title of 1 to {Number(FindingLimits.MaxTitleLength)} characters.";
        }

        if (!IsRelativePath(finding.Path))
        {
            return "needs a path relative to the repository root, without a leading slash or '..'.";
        }

        if (finding.Line < 1)
        {
            return "needs a line that is a positive whole number.";
        }

        if (string.IsNullOrWhiteSpace(finding.Why))
        {
            return "needs a why that says what is wrong.";
        }

        if (string.IsNullOrWhiteSpace(finding.Fix))
        {
            return "needs a fix.";
        }

        if ((TooLong("why", finding.Why) ?? TooLong("fix", finding.Fix) ?? TooLong("context", finding.Context)) is { } tooLong)
        {
            return tooLong;
        }

        if (finding.Evidence is { Length: > FindingLimits.MaxEvidenceLength } evidence)
        {
            return $"has evidence of {Number(evidence.Length)} characters; at most {Number(FindingLimits.MaxEvidenceLength)} {Allowed}";
        }

        if ((string.IsNullOrWhiteSpace(finding.Rule) ? 0 : finding.Rule.Trim().Length) > FindingLimits.MaxRuleLength)
        {
            return $"needs a rule of 1 to {Number(FindingLimits.MaxRuleLength)} characters, or none.";
        }

        return null;
    }

    private static string? TooLong(string field, string? text) =>
        text is { Length: > FindingLimits.MaxTextLength }
            ? $"has a {field} of {Number(text.Length)} characters; at most {Number(FindingLimits.MaxTextLength)} {Allowed}"
            : null;

    private static bool IsRelativePath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && path.Length <= FindingLimits.MaxPathLength
        && !path.StartsWith('/')
        && !path.Contains('\\', StringComparison.Ordinal)
        && !path.Contains("..", StringComparison.Ordinal)
        && path.Split('/').All(segment => segment.Length > 0 && segment != "." && segment != "..");

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
