using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scout.Review;

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
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<ReviewCheck>? Checks = null,
    ReviewTests? Tests = null,
    bool? SelfAuthored = null,
    Offer? Offer = null)
{

    /// <summary>The checks read with the head, counted for the offer; never written into the block.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    internal OfferChecks? CheckCounts { get; init; }

    /// <summary>Whether the pull request moved past the commit reviewed when the checks were read; never written into the block.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    internal bool HeadMoved { get; init; }

    internal const int MaxKnown = 20;
    internal const int MaxKnownLength = 300;
    internal const string Audit = "audit";

    internal const string InvalidRepository =
        "The repository must be given as owner/repo, each part made of letters, digits, dots, hyphens or underscores.";

    internal const string InvalidNumber = "The pull request number must be positive.";
    internal const string InvalidVerdict = "The verdict must not be empty.";
    internal const string NoCommit = "The review needs the head commit it read, the SHA get_pull_request answered; the card pins the verdict to it.";
    internal const string InvalidFindings = "The findings must be a JSON array of 1 to 50 findings.";
    internal const string InvalidReviewFindings = "The findings must be a JSON array of at most 50 findings; [] when the review found nothing.";

    internal const string InvalidKnown =
        "The known limitations must be a JSON array of at most 20 texts, each 1 to 300 characters.";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

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

        if (kind is null && string.IsNullOrWhiteSpace(commit))
        {
            return (null, NoCommit);
        }

        if (string.IsNullOrWhiteSpace(verdict))
        {
            return (null, InvalidVerdict);
        }

        var invalidFindings = kind is null ? InvalidReviewFindings : InvalidFindings;
        var (findings, problem) = ParseFindings(findingsJson, invalidFindings);
        if (findings is null)
        {
            return (null, problem);
        }

        if (kind is not null && findings.Count == 0)
        {
            return (null, invalidFindings);
        }

        if (!TryKnown(knownJson, out var known))
        {
            return (null, InvalidKnown);
        }

        return (new ReviewFindings(
            kind, $"{parts[0]}/{parts[1]}", number, Blank(commit), verdict.Trim(), Blank(read), known, findings), null);
    }

    /// <summary>
    /// The findings of a JSON array that pass <see cref="FindingValidation"/>; otherwise null and one sentence naming the
    /// first problem, <paramref name="invalid"/> when the text is not a JSON array.
    /// </summary>
    internal static (IReadOnlyList<Finding>? Findings, string? Problem) ParseFindings(string? json, string invalid)
    {
        JsonElement array;
        try
        {
            using var document = JsonDocument.Parse(json ?? string.Empty);
            array = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return (null, invalid);
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            return (null, invalid);
        }

        var elements = array.EnumerateArray().ToList();
        var findings = elements.Select(Candidate).ToList();
        if (findings.Count > FindingLimits.MaxFindings)
        {
            return (null, FindingValidation.ProblemOf(findings!));
        }

        for (var index = 0; index < findings.Count; index++)
        {
            var problem = findings[index] is { } finding
                ? FindingValidation.ProblemOf(finding) ?? TypeProblemOf(elements[index])
                : "must be a JSON object.";
            if (problem is not null)
            {
                return (null, $"Finding {(index + 1).ToString(CultureInfo.InvariantCulture)} {problem}");
            }
        }

        return FindingValidation.ProblemOf(findings!) is { } repeated ? (null, repeated) : (findings!, null);
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

    /// <summary>The finding an element of the array names, its line 0 when it is not a whole number; null when it is not an object.</summary>
    private static Finding? Candidate(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
            ? new Finding(
                Text(element, "id")!, Text(element, "severity")!, Text(element, "title")!, Text(element, "path")!,
                element.TryGetProperty("line", out var line) && line.ValueKind == JsonValueKind.Number && line.TryGetInt32(out var value) ? value : 0,
                Blank(Text(element, "context")), Text(element, "why")!, Text(element, "fix")!, Blank(Text(element, "rule")),
                string.IsNullOrWhiteSpace(Text(element, "evidence")) ? null : Text(element, "evidence"))
            : null;

    /// <summary>A context or a rule that is neither text nor null, which <see cref="Candidate"/> reads as none.</summary>
    private static string? TypeProblemOf(JsonElement element)
    {
        if (element.TryGetProperty("context", out var context) && context.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            return "has a context that is not text.";
        }

        return element.TryGetProperty("rule", out var rule) && rule.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)
            ? $"needs a rule of 1 to {FindingLimits.MaxRuleLength.ToString(CultureInfo.InvariantCulture)} characters, or none."
            : null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>One of the pull request's checks, as the card shows it; the conclusion is written as null while it runs.</summary>
internal sealed record ReviewCheck(
    string Name,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Conclusion,
    string Url);

/// <summary>
/// What the container run of the project's tests said, as the card shows it; <c>unrunnable</c>, written only when set,
/// says the tests that failed could not run for want of a Docker daemon.
/// </summary>
internal sealed record ReviewTests(
    bool Ran, string? Reason, string? Image, string? Command, int? ExitCode, double? DurationSeconds, string? Output, string? Note, string? Unrunnable = null);
