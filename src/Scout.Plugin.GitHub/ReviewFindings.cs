using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Scout.Plugin.GitHub;

/// <summary>
/// A code review's findings in the shape Scout Desktop renders as cards, validated by the rules the desktop's parser
/// applies, so a block Scout attaches to a reply is never one the desktop leaves as a code fence.
/// </summary>
internal sealed partial record ReviewFindings(
    string Repository,
    int Number,
    string? Commit,
    string Verdict,
    string? Read,
    IReadOnlyList<ReviewFinding> Findings)
{
    internal const int MaxFindings = 50;
    internal const int MaxTitleLength = 200;

    internal const string InvalidRepository =
        "The repository must be given as owner/repo, each part made of letters, digits, dots, hyphens or underscores.";

    internal const string InvalidNumber = "The pull request number must be positive.";
    internal const string InvalidVerdict = "The verdict must not be empty.";
    internal const string InvalidFindings = "The findings must be a JSON array of 1 to 50 findings.";

    private static readonly HashSet<string> Severities = new(StringComparer.Ordinal) { "block", "fix", "nit" };

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [GeneratedRegex("^[A-Za-z0-9-]{1,40}$")]
    private static partial Regex FindingId();

    /// <summary>The fenced <c>scout-findings</c> block: the info string, one line of JSON, the closing fence.</summary>
    internal string ToBlock() => "```scout-findings\n" + JsonSerializer.Serialize(this, Json) + "\n```";

    /// <summary>
    /// The findings when every value passes the desktop's rules; otherwise null and one sentence naming the first
    /// problem, safe to show the model and the user.
    /// </summary>
    internal static (ReviewFindings? Findings, string? Problem) Parse(
        string? repository, int number, string? commit, string? verdict, string? read, string? findingsJson)
    {
        var parts = (repository ?? string.Empty).Trim().Split('/');
        if (parts.Length != 2 || !GitHubNames.IsValidRepository(parts[0], parts[1]))
        {
            return (null, InvalidRepository);
        }

        if (number <= 0)
        {
            return (null, InvalidNumber);
        }

        if (string.IsNullOrWhiteSpace(verdict))
        {
            return (null, InvalidVerdict);
        }

        JsonElement array;
        try
        {
            using var document = JsonDocument.Parse(findingsJson ?? string.Empty);
            array = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return (null, InvalidFindings);
        }

        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is 0 or > MaxFindings)
        {
            return (null, InvalidFindings);
        }

        var findings = new List<ReviewFinding>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in array.EnumerateArray())
        {
            var position = (findings.Count + 1).ToString(CultureInfo.InvariantCulture);
            if (ParseFinding(element) is not { } finding)
            {
                return (null, $"Finding {position} {ProblemOf(element)}");
            }

            if (!ids.Add(finding.Id))
            {
                return (null, $"Finding {position} repeats the id {finding.Id}; each id must be unique.");
            }

            findings.Add(finding);
        }

        return (new ReviewFindings(
            $"{parts[0]}/{parts[1]}", number, Blank(commit), verdict.Trim(), Blank(read), findings), null);
    }

    private static ReviewFinding? ParseFinding(JsonElement element) =>
        ProblemOf(element) is null
            ? new ReviewFinding(
                Text(element, "id")!, Text(element, "severity")!, Text(element, "title")!, Text(element, "path")!,
                element.GetProperty("line").GetInt32(), Blank(Text(element, "context")), Text(element, "why")!, Text(element, "fix")!)
            : null;

    private static string? ProblemOf(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return "must be a JSON object.";
        }

        if (Text(element, "id") is not { } id || !FindingId().IsMatch(id))
        {
            return "needs an id of 1 to 40 letters, digits or hyphens.";
        }

        if (Text(element, "severity") is not { } severity || !Severities.Contains(severity))
        {
            return "needs a severity of block, fix or nit.";
        }

        if (Text(element, "title") is not { } title || string.IsNullOrWhiteSpace(title) || title.Length > MaxTitleLength)
        {
            return "needs a title of 1 to 200 characters.";
        }

        if (Text(element, "path") is not { } path || !GitHubNames.IsValidPath(path) || path.Contains("..", StringComparison.Ordinal))
        {
            return "needs a path relative to the repository root, without a leading slash or '..'.";
        }

        if (!element.TryGetProperty("line", out var line) || line.ValueKind != JsonValueKind.Number
            || !line.TryGetInt32(out var value) || value < 1)
        {
            return "needs a line that is a positive whole number.";
        }

        if (element.TryGetProperty("context", out var context) && context.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            return "has a context that is not text.";
        }

        if (string.IsNullOrWhiteSpace(Text(element, "why")))
        {
            return "needs a why that says what is wrong.";
        }

        return string.IsNullOrWhiteSpace(Text(element, "fix")) ? "needs a fix." : null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>One finding of a review, anchored to a line of the file after the change.</summary>
internal sealed record ReviewFinding(
    string Id, string Severity, string Title, string Path, int Line, string? Context, string Why, string Fix);
