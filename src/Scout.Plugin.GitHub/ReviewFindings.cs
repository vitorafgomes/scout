using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Scout.Plugin.GitHub;

/// <summary>
/// A code review's or an audit's findings in the shape Scout Desktop renders as cards, validated by the rules the
/// desktop's parser applies, so a block Scout attaches to a reply is never one the desktop leaves as a code fence. A
/// review carries no kind and the pull request number; an audit carries the kind <c>audit</c>, no number, and the
/// limitations the project already documents.
/// </summary>
internal sealed partial record ReviewFindings(
    string? Kind,
    string Repository,
    int? Number,
    string? Commit,
    string Verdict,
    string? Read,
    IReadOnlyList<string>? Known,
    IReadOnlyList<ReviewFinding> Findings)
{
    internal const int MaxFindings = 50;
    internal const int MaxTitleLength = 200;
    internal const int MaxRuleLength = 300;
    internal const int MaxKnown = 20;
    internal const int MaxKnownLength = 300;
    internal const string Audit = "audit";

    internal const string InvalidRepository =
        "The repository must be given as owner/repo, each part made of letters, digits, dots, hyphens or underscores.";

    internal const string InvalidNumber = "The pull request number must be positive.";
    internal const string InvalidVerdict = "The verdict must not be empty.";
    internal const string InvalidFindings = "The findings must be a JSON array of 1 to 50 findings.";

    internal const string InvalidKnown =
        "The known limitations must be a JSON array of at most 20 texts, each 1 to 300 characters.";

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
    /// A review's findings when every value passes the desktop's rules; otherwise null and one sentence naming the
    /// first problem, safe to show the model and the user.
    /// </summary>
    internal static (ReviewFindings? Findings, string? Problem) Parse(
        string? repository, int number, string? commit, string? verdict, string? read, string? findingsJson) =>
        Parse(null, repository, number, commit, verdict, read, findingsJson, null);

    /// <summary>
    /// An audit's findings and the limitations the project already documents when every value passes the desktop's
    /// rules; otherwise null and one sentence naming the first problem.
    /// </summary>
    internal static (ReviewFindings? Findings, string? Problem) ParseAudit(
        string? repository, string? commit, string? verdict, string? read, string? findingsJson, string? knownJson) =>
        Parse(Audit, repository, null, commit, verdict, read, findingsJson, knownJson);

    private static (ReviewFindings? Findings, string? Problem) Parse(
        string? kind, string? repository, int? number, string? commit, string? verdict, string? read, string? findingsJson, string? knownJson)
    {
        var parts = (repository ?? string.Empty).Trim().Split('/');
        if (parts.Length != 2 || !GitHubNames.IsValidRepository(parts[0], parts[1]))
        {
            return (null, InvalidRepository);
        }

        if (kind is null && number is not > 0)
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

        if (!TryKnown(knownJson, out var known))
        {
            return (null, InvalidKnown);
        }

        return (new ReviewFindings(
            kind, $"{parts[0]}/{parts[1]}", number, Blank(commit), verdict.Trim(), Blank(read), known, findings), null);
    }

    private static bool TryKnown(string? knownJson, out IReadOnlyList<string>? known)
    {
        known = null;
        if (string.IsNullOrWhiteSpace(knownJson))
        {
            return true;
        }

        JsonElement array;
        try
        {
            using var document = JsonDocument.Parse(knownJson);
            array = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return false;
        }

        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaxKnown)
        {
            return false;
        }

        var items = new List<string>();
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String || Blank(element.GetString()) is not { Length: <= MaxKnownLength } item)
            {
                return false;
            }

            items.Add(item);
        }

        known = items.Count == 0 ? null : items;
        return true;
    }

    private static ReviewFinding? ParseFinding(JsonElement element) =>
        ProblemOf(element) is null
            ? new ReviewFinding(
                Text(element, "id")!, Text(element, "severity")!, Text(element, "title")!, Text(element, "path")!,
                element.GetProperty("line").GetInt32(), Blank(Text(element, "context")), Text(element, "why")!, Text(element, "fix")!,
                Blank(Text(element, "rule")))
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

        if (string.IsNullOrWhiteSpace(Text(element, "fix")))
        {
            return "needs a fix.";
        }

        if (element.TryGetProperty("rule", out var rule)
            && (rule.ValueKind is not (JsonValueKind.String or JsonValueKind.Null) || (Blank(rule.GetString())?.Length ?? 0) > MaxRuleLength))
        {
            return "needs a rule of 1 to 300 characters, or none.";
        }

        return null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// One finding of a review or an audit, anchored to a line of the file, with the project rule it departs from when
/// one is named.
/// </summary>
internal sealed record ReviewFinding(
    string Id, string Severity, string Title, string Path, int Line, string? Context, string Why, string Fix, string? Rule);
