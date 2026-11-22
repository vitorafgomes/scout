using System.Text.RegularExpressions;

namespace Scout.Review;

/// <summary>A finding as Scout's own comment on its line carries it, read back from the comment's text.</summary>
/// <param name="Severity">One of <see cref="Severities.All"/>.</param>
/// <param name="Title">What the finding names.</param>
/// <param name="Why">What is wrong.</param>
/// <param name="Context">Why it matters, or null.</param>
/// <param name="Fix">The proposed fix.</param>
/// <param name="Rule">The project rule or document it departs from, or null.</param>
public sealed record CommentedFinding(string Severity, string Title, string Why, string? Context, string Fix, string? Rule);

/// <summary>
/// The one form of Scout's comment on a finding's line, written and read back here: its severity and title, what is
/// wrong, why it matters, the fix and its rule. Pure: no call, no file.
/// </summary>
public static partial class FindingComment
{
    /// <summary>The comment on <paramref name="finding"/>'s line.</summary>
    public static string Write(Finding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        List<string> lines = [$"{Icon(finding.Severity)} **{Word(finding.Severity)}** · {finding.Title}", string.Empty, $"**What is wrong:** {finding.Why}"];
        if (!string.IsNullOrWhiteSpace(finding.Context))
        {
            lines.AddRange([string.Empty, $"**Why it matters:** {finding.Context}"]);
        }

        lines.AddRange([string.Empty, $"**Proposed fix:** {finding.Fix}"]);
        if (!string.IsNullOrWhiteSpace(finding.Rule))
        {
            lines.AddRange([string.Empty, $"**Rule or document:** {finding.Rule}"]);
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// The finding <paramref name="body"/> carries when it is in the form <see cref="Write"/> gives, its icon and word
    /// naming the same severity; null for any other text, which is not one of Scout's findings.
    /// </summary>
    public static CommentedFinding? Read(string? body)
    {
        var match = Form().Match((body ?? string.Empty).ReplaceLineEndings("\n").Trim());
        if (!match.Success)
        {
            return null;
        }

        var severity = Severities.All.FirstOrDefault(s => Icon(s) == match.Groups["icon"].Value && Word(s) == match.Groups["word"].Value);
        return severity is null
            ? null
            : new CommentedFinding(
                severity, match.Groups["title"].Value, match.Groups["why"].Value,
                match.Groups["context"].Success ? match.Groups["context"].Value : null,
                match.Groups["fix"].Value,
                match.Groups["rule"].Success ? match.Groups["rule"].Value : null);
    }

    /// <summary>The GitHub shortcode a severity's comment starts with.</summary>
    public static string Icon(string severity) => severity switch
    {
        Severities.Block => ":no_entry:",
        Severities.Fix => ":wrench:",
        _ => ":bulb:",
    };

    /// <summary>The word a severity's comment names it by.</summary>
    public static string Word(string severity) => severity switch
    {
        Severities.Block => "Blocking",
        Severities.Fix => "Fix",
        _ => "Suggestion",
    };

    [GeneratedRegex(
        @"\A(?<icon>:no_entry:|:wrench:|:bulb:) \*\*(?<word>Blocking|Fix|Suggestion)\*\* · (?<title>[^\n]+)\n\n\*\*What is wrong:\*\* (?<why>.+?)(?:\n\n\*\*Why it matters:\*\* (?<context>.+?))?\n\n\*\*Proposed fix:\*\* (?<fix>.+?)(?:\n\n\*\*Rule or document:\*\* (?<rule>.+))?\z",
        RegexOptions.Singleline | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Form();
}
