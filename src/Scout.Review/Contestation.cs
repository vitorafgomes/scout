using System.Globalization;
using System.Text.Json;

namespace Scout.Review;

/// <summary>The answers Scout's second reading gives a finding, and the state a finding is recorded in.</summary>
public static class ContestStatus
{
    /// <summary>The supplied code and facts support the finding and nothing supplied contradicts it.</summary>
    public const string Holds = "holds";

    /// <summary>The supplied code or facts establish that the finding's claim is false, or that the behaviour is the design.</summary>
    public const string Refuted = "refuted";

    /// <summary>The finding's claim is true, and the repository itself records it as known or later work.</summary>
    public const string Noted = "noted";

    /// <summary>Something needed to decide is missing.</summary>
    public const string Uncertain = "uncertain";

    /// <summary>The recorded state of a finding the second reading has not answered for.</summary>
    public const string Open = "open";

    /// <summary>The recorded state of a finding the second reading says holds.</summary>
    public const string Held = "held";

    /// <summary>The recorded state for <paramref name="status"/>: <see cref="Held"/> for <see cref="Holds"/>, otherwise the status itself.</summary>
    public static string StateOf(string status) => status == Holds ? Held : status;

    /// <summary>The status a recorded <paramref name="state"/> stands for; null when the finding was not contested.</summary>
    public static string? StatusOf(string? state) => state switch
    {
        Held => Holds,
        Refuted or Noted or Uncertain => state,
        _ => null,
    };
}

/// <summary>What the second reading answered for one finding.</summary>
/// <param name="Status">One of <see cref="ContestStatus.Holds"/>, <see cref="ContestStatus.Refuted"/>, <see cref="ContestStatus.Noted"/> or <see cref="ContestStatus.Uncertain"/>.</param>
/// <param name="Reason">Why, in one or two sentences of at most <see cref="Contestation.MaxReasonLength"/> characters.</param>
/// <param name="Severity">For a finding that holds, the severity the second reading concluded by <see cref="SeverityRule"/>; null to keep the finding's.</param>
/// <param name="Scenario">For a finding that holds, the concrete scenario in which it goes wrong; null otherwise.</param>
public sealed record ContestOutcome(string Status, string Reason, string? Severity = null, string? Scenario = null);

/// <summary>
/// The rule by which the second reading decides the severity of a finding that holds, written once for the instructions
/// and for the code that checks the answer: block only for one of <see cref="BlockReasons"/>.
/// </summary>
public static class SeverityRule
{
    /// <summary>The ends of a scenario that make a finding blocking; an answer names one to conclude block.</summary>
    public static IReadOnlyList<string> BlockReasons { get; } =
        ["lost or corrupted data", "a security or secret exposure", "a build or a test run that fails", "a core operation that cannot complete"];

    /// <summary>The end of a scenario that makes a finding a fix.</summary>
    public const string FixReason = "a wrong result or a missing protection";

    /// <summary>What makes a finding a nit.</summary>
    public const string NitReason = "wording, documentation or style";

    /// <summary>The rule as the instructions give it.</summary>
    public static string Text { get; } =
        "Severity of a finding that holds: \"block\" only when its scenario ends in "
        + string.Join(", ", BlockReasons.Select(r => $"\"{r}\"")) + $"; \"fix\" for \"{FixReason}\" without those; \"nit\" for \"{NitReason}\". "
        + "Give the severity in \"severity\" and, in \"because\", the words above it rests on; a block without one of the block reasons is read as fix.";

    /// <summary>
    /// The severity an answer concludes: <paramref name="severity"/> when it is known, except a block whose
    /// <paramref name="because"/> is not one of <see cref="BlockReasons"/>, which is a fix; null when none is known.
    /// </summary>
    public static string? Concluded(string? severity, string? because) =>
        !Severities.IsKnown(severity) ? null
        : Severities.IsBlocking(severity) && !BlockReasons.Contains(because?.Trim(), StringComparer.OrdinalIgnoreCase) ? Severities.Fix
        : severity;
}

/// <summary>
/// The rules of Scout's second reading of a review's findings: how its answer is checked, how an answer changes a finding,
/// how findings are grouped for it, and the card's notes on what it did. Pure: no model call, no file, no store.
/// </summary>
public static partial class Contestation
{
    /// <summary>The findings of one request at most.</summary>
    public const int MaxGroupFindings = 4;

    /// <summary>The requests of one review at most.</summary>
    public const int MaxGroups = 15;

    /// <summary>The time the second reading of one review may take in all; each group has its own bound within it.</summary>
    public static readonly TimeSpan MaxTime = TimeSpan.FromMinutes(30);

    /// <summary>The characters of one reason at most.</summary>
    public const int MaxReasonLength = 300;

    /// <summary>What an unconfirmed finding's title starts with.</summary>
    public const string UnconfirmedPrefix = "Unconfirmed: ";

    /// <summary>What a noted finding's title starts with: true, and recorded by the project as later work.</summary>
    public const string NotedPrefix = "Noted: ";

    /// <summary>What the record a noted finding rests on starts with when it is added to the finding's why.</summary>
    public const string RecordPrefix = "Recorded by the project: ";

    /// <summary>The reason of a finding whose entry in the answer is missing, repeated or malformed.</summary>
    public const string NoValidEntry = "Scout's second reading gave no valid answer for this finding.";

    /// <summary>The reason of a finding the second reading said holds without the scenario that shows it.</summary>
    public const string NoScenario = "Scout's second reading said this finding holds but wrote no scenario in which it goes wrong.";

    /// <summary>What the scenario starts with when it is added to a finding's why.</summary>
    public const string ScenarioPrefix = "Scenario: ";

    /// <summary>
    /// The outcome of each of <paramref name="ids"/> in <paramref name="answer"/>, which must be one JSON array (a code fence
    /// around it is allowed) of objects with <c>id</c>, <c>status</c> and <c>reason</c>. An id with exactly one entry whose
    /// status is one of the three and that gives a reason gets that entry, its reason and scenario cut to
    /// <see cref="MaxReasonLength"/> when longer; an id whose entry is missing, repeated, not an object, of an unknown
    /// status or without a reason is <see cref="ContestStatus.Uncertain"/>; an entry for another id is ignored.
    /// Null when the answer is not such an array, so nothing of it is used.
    /// </summary>
    public static IReadOnlyDictionary<string, ContestOutcome>? Read(string? answer, IReadOnlyList<string> ids)
    {
        if (Entries(answer) is not { } array)
        {
            return null;
        }

        var entries = new Dictionary<string, List<ContestOutcome?>>(StringComparer.Ordinal);
        foreach (var entry in array)
        {
            if (Text(entry, "id") is { } id)
            {
                var status = Text(entry, "status");
                var reason = Bounded(Text(entry, "reason")?.Trim());
                var scenario = Bounded(Text(entry, "scenario")?.Trim());
                var valid = status is ContestStatus.Holds or ContestStatus.Refuted or ContestStatus.Noted or ContestStatus.Uncertain && reason is { Length: > 0 };
                var outcome = !valid ? null
                    : status != ContestStatus.Holds ? new ContestOutcome(status!, reason!)
                    : scenario is not { Length: > 0 } ? new ContestOutcome(ContestStatus.Uncertain, NoScenario)
                    : new ContestOutcome(status, reason!, SeverityRule.Concluded(Text(entry, "severity")?.Trim(), Text(entry, "because")), scenario);
                (entries.TryGetValue(id, out var list) ? list : entries[id] = []).Add(outcome);
            }
        }

        return ids.ToDictionary(
            id => id,
            id => entries.TryGetValue(id, out var list) && list is [{ } only] ? only : new ContestOutcome(ContestStatus.Uncertain, NoValidEntry),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// The objects of <paramref name="answer"/> when it is one JSON array, a code fence around it allowed; null otherwise.
    /// Shared by every reading whose answer is an array of entries.
    /// </summary>
    internal static IReadOnlyList<JsonElement>? Entries(string? answer)
    {
        try
        {
            using var parsed = JsonDocument.Parse(Unfenced(answer ?? string.Empty));
            return parsed.RootElement.ValueKind == JsonValueKind.Array
                ? [.. parsed.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Select(e => e.Clone())]
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The text of <paramref name="name"/> in <paramref name="entry"/>, null when it is not text.</summary>
    internal static string? Text(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// <paramref name="text"/> within <see cref="MaxReasonLength"/>: a longer text is cut at the last word boundary that
    /// leaves room for <c>...</c>, and ends with it.
    /// </summary>
    internal static string? Bounded(string? text)
    {
        if (text is not { Length: > MaxReasonLength })
        {
            return text;
        }

        var cut = text[..(MaxReasonLength - 3)];
        var space = cut.LastIndexOf(' ');
        return (space > 0 ? cut[..space] : cut).TrimEnd() + "...";
    }

    private static string Unfenced(string answer)
    {
        var text = answer.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal) || !text.EndsWith("```", StringComparison.Ordinal) || text.Length < 6)
        {
            return text;
        }

        var start = text.IndexOf('\n', StringComparison.Ordinal);
        return start < 0 ? text : text[(start + 1)..^3];
    }

    /// <summary>
    /// The finding as the card shows it after <paramref name="outcome"/>: null when refuted; when noted, a
    /// <see cref="Severities.Nit"/> titled with <see cref="NotedPrefix"/>, its why ending with the record the reason names
    /// after <see cref="RecordPrefix"/>; when uncertain, never blocking
    /// (a blocking severity becomes <see cref="Severities.Fix"/>) and titled with <see cref="UnconfirmedPrefix"/>; when it
    /// holds, with the severity the second reading concluded, or, when it gave none, the finding's own with a block lowered to
    /// fix, since a block must be concluded with its reason, and its scenario as the last sentence of its why
    /// (<see cref="WithScenario"/>); otherwise as it is.
    /// </summary>
    public static Finding? Apply(Finding finding, ContestOutcome? outcome) => outcome?.Status switch
    {
        ContestStatus.Refuted => null,
        ContestStatus.Noted => finding with
        {
            Severity = Severities.Nit,
            Title = NotedTitle(finding.Title),
            Why = WithTail(finding.Why, RecordPrefix, outcome.Reason),
        },
        ContestStatus.Uncertain => finding with
        {
            Severity = Severities.IsBlocking(finding.Severity) ? Severities.Fix : finding.Severity,
            Title = UnconfirmedTitle(finding.Title),
        },
        ContestStatus.Holds => finding with
        {
            Severity = outcome.Severity ?? (Severities.IsBlocking(finding.Severity) ? Severities.Fix : finding.Severity),
            Why = WithScenario(finding.Why, outcome.Scenario),
        },
        _ => finding,
    };

    /// <summary>
    /// <paramref name="why"/> with <paramref name="scenario"/> as its last sentence, after <see cref="ScenarioPrefix"/>, within
    /// <see cref="FindingLimits.MaxTextLength"/>: the scenario is kept whole and the why before it cut, ending with <c>...</c>.
    /// </summary>
    public static string WithScenario(string why, string? scenario) => WithTail(why, ScenarioPrefix, scenario);

    private static string WithTail(string why, string prefix, string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || why.EndsWith(prefix + text, StringComparison.Ordinal))
        {
            return why;
        }

        var tail = " " + prefix + text;
        var room = FindingLimits.MaxTextLength - tail.Length;
        return (why.Length <= room ? why : string.Concat(why.AsSpan(0, Math.Max(0, room - 3)), "...")) + tail;
    }

    /// <summary>
    /// <paramref name="findings"/>, given in the card's order, in groups: the findings of one file kept together where the
    /// file first appears, at most <see cref="MaxGroupFindings"/> per group, and a group closed before the size of its
    /// findings' material (<paramref name="size"/>) would pass <paramref name="bound"/>; a finding larger than the bound
    /// has a group of its own.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<Finding>> Groups(IReadOnlyList<Finding> findings, Func<Finding, int> size, int bound)
    {
        var groups = new List<IReadOnlyList<Finding>>();
        var group = new List<Finding>();
        var used = 0;
        foreach (var finding in findings.GroupBy(f => f.Path, StringComparer.Ordinal).SelectMany(file => file))
        {
            var length = size(finding);
            if (group.Count > 0 && (group.Count == MaxGroupFindings || used + length > bound))
            {
                groups.Add(group);
                (group, used) = ([], 0);
            }

            group.Add(finding);
            used += length;
        }

        if (group.Count > 0)
        {
            groups.Add(group);
        }

        return groups;
    }

    /// <summary>
    /// The card's notes on the second reading, each one note counted over the whole review: the findings refuted, with as
    /// many of their titles and reasons as fit the note's bound (<see cref="RefutedNote"/>); the findings that could not be
    /// confirmed; the findings kept as notes; the severities changed; and one line per reason the second reading did not
    /// run for some findings, naming their titles after the reason, within the note's bound.
    /// </summary>
    public static IReadOnlyList<string> Notes(
        IReadOnlyList<(string Title, string Reason)> refuted, int uncertain, IReadOnlyList<(int Findings, string Reason, IReadOnlyList<string> Titles)> notContested, int severitiesChanged = 0, int noted = 0)
    {
        var notes = new List<string>();
        if (RefutedNote(refuted) is { } refutedNote)
        {
            notes.Add(refutedNote);
        }

        if (uncertain > 0)
        {
            notes.Add(uncertain == 1
                ? "1 finding could not be confirmed by Scout's second reading; it is kept as unconfirmed and does not block."
                : string.Create(CultureInfo.InvariantCulture, $"{uncertain} findings could not be confirmed by Scout's second reading; they are kept as unconfirmed and do not block."));
        }

        if (noted > 0)
        {
            notes.Add(noted == 1
                ? "1 finding is true and already recorded by the project as later work; it is kept as a note."
                : string.Create(CultureInfo.InvariantCulture, $"{noted} findings are true and already recorded by the project as later work; they are kept as notes."));
        }

        if (severitiesChanged > 0)
        {
            notes.Add(severitiesChanged == 1
                ? "1 finding's severity was changed by Scout's second reading."
                : string.Create(CultureInfo.InvariantCulture, $"{severitiesChanged} findings' severities were changed by Scout's second reading."));
        }

        notes.AddRange(notContested.Where(n => n.Findings > 0).Select(n => ReviewNotes.Cut(string.Create(
            CultureInfo.InvariantCulture, $"Scout's second reading did not run for {Findings(n.Findings)}: {n.Reason}{(n.Titles.Count == 0 ? string.Empty : $" Kept as the first reading gave {(n.Titles.Count == 1 ? "it" : "them")}: {string.Join("; ", n.Titles)}.")}"))));
        return notes;
    }

    /// <summary>
    /// The one note on every finding the second reading refuted: their count, then each one's title and reason in the order
    /// given, as many as fit <see cref="FindingLimits.MaxNoteLength"/>, and "and N more" for the rest; the first is cut to fit
    /// when it alone does not. Null when none was refuted.
    /// </summary>
    public static string? RefutedNote(IReadOnlyList<(string Title, string Reason)> refuted)
    {
        if (refuted.Count == 0)
        {
            return null;
        }

        var head = string.Create(CultureInfo.InvariantCulture, $"{Findings(refuted.Count)} refuted by Scout's second reading: ");
        var pairs = refuted.Select(r => $"{r.Title}: {r.Reason.Trim().TrimEnd('.')}").ToList();
        string Tail(int shown) => shown < pairs.Count ? string.Create(CultureInfo.InvariantCulture, $"; and {pairs.Count - shown} more.") : ".";
        for (var shown = pairs.Count; shown > 1; shown--)
        {
            var text = head + string.Join("; ", pairs.Take(shown)) + Tail(shown);
            if (text.Length <= FindingLimits.MaxNoteLength)
            {
                return text;
            }
        }

        var room = FindingLimits.MaxNoteLength - head.Length - Tail(1).Length;
        return head + (pairs[0].Length <= room ? pairs[0] : string.Concat(pairs[0].AsSpan(0, Math.Max(0, room - 3)), "...")) + Tail(1);
    }

    /// <summary><paramref name="title"/> marked as not confirmed by the second reading, once, within its bound.</summary>
    public static string UnconfirmedTitle(string title) =>
        title.StartsWith(UnconfirmedPrefix, StringComparison.Ordinal) ? title : Cut(UnconfirmedPrefix + title, FindingLimits.MaxTitleLength);

    /// <summary><paramref name="title"/> marked as true and recorded by the project as later work, once, within its bound.</summary>
    public static string NotedTitle(string title) =>
        title.StartsWith(NotedPrefix, StringComparison.Ordinal) ? title : Cut(NotedPrefix + title, FindingLimits.MaxTitleLength);

    /// <summary>
    /// <paramref name="finding"/> as a first reading may give it: without any leading <see cref="UnconfirmedPrefix"/> or
    /// <see cref="NotedPrefix"/>, in any case, so only the second reading can mark a finding unconfirmed or noted and lower
    /// its weight.
    /// </summary>
    public static Finding FirstReading(Finding finding)
    {
        var title = finding.Title.TrimStart();
        string? prefix;
        while ((prefix = Array.Find([UnconfirmedPrefix.TrimEnd(), NotedPrefix.TrimEnd()], p => title.StartsWith(p, StringComparison.OrdinalIgnoreCase))) is not null)
        {
            title = title[prefix.Length..].TrimStart();
        }

        return title.Length == finding.Title.Length ? finding : finding with { Title = title };
    }

    /// <summary>The reason of a refutation or a note that cites a file or a line the pull request's head does not hold.</summary>
    public static string CitesNothingReal(string path, int line) =>
        string.Create(CultureInfo.InvariantCulture, $"Scout's second reading cited {path} at line {line}, which the pull request's head does not hold, so its reading is not confirmed.");

    /// <summary>
    /// <paramref name="outcome"/> with its citations checked: a refutation or a note whose reason cites a file and line
    /// (<see cref="Cited"/>) that resolves to one file (<see cref="Resolve"/>) with fewer lines than the line cited is
    /// <see cref="ContestStatus.Uncertain"/>, its reason naming the file as resolved; a cited name that resolves to no file
    /// or to more than one is no evidence, and the outcome stands; any other outcome as it is. Only that the line exists is
    /// checked, never what it says.
    /// </summary>
    public static ContestOutcome Checked(ContestOutcome outcome, string findingPath, IReadOnlyCollection<string> changed, Func<string, int?> lines)
    {
        if (outcome.Status is not (ContestStatus.Refuted or ContestStatus.Noted))
        {
            return outcome;
        }

        foreach (var (cited, line) in Cited(outcome.Reason))
        {
            if (Resolve(cited, findingPath, changed, lines) is { } file && line > lines(file))
            {
                return new ContestOutcome(ContestStatus.Uncertain, CitesNothingReal(file, line));
            }
        }

        return outcome;
    }

    /// <summary>
    /// The one file a cited name stands for, tried in this order: the name as a path from the root, when the head holds it;
    /// the finding's own path, when the name is that path or a trailing part of it at a folder boundary; the one changed
    /// file whose path ends with the name at a folder boundary. Null when none of these holds, or when two changed files do.
    /// </summary>
    public static string? Resolve(string cited, string findingPath, IReadOnlyCollection<string> changed, Func<string, int?> lines)
    {
        if (lines(cited) is not null)
        {
            return cited;
        }

        if (EndsAt(findingPath, cited))
        {
            return lines(findingPath) is null ? null : findingPath;
        }

        return changed.Where(c => EndsAt(c, cited)).Distinct(StringComparer.Ordinal).ToList() is [var one] && lines(one) is not null ? one : null;

        static bool EndsAt(string path, string name) =>
            path == name || path.EndsWith("/" + name, StringComparison.Ordinal);
    }

    /// <summary>
    /// The files and lines <paramref name="reason"/> cites: a relative path with an extension followed by <c>:N</c>,
    /// <c>#LN</c>, <c> line N</c> or <c> lines N</c>, the first line of a range; a leading <c>./</c> or <c>/</c> is dropped.
    /// </summary>
    public static IReadOnlyList<(string Path, int Line)> Cited(string reason) =>
        [.. CitationPattern().Matches(reason)
            .Select(m => (m.Groups["path"].Value is var p && p.StartsWith("./", StringComparison.Ordinal) ? p[2..] : p.TrimStart('/'), int.TryParse(m.Groups["line"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0))
            .Where(c => c.Item1.Length > 0 && c.Item2 > 0)
            .Distinct()];

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<path>[A-Za-z0-9_./-]*[A-Za-z0-9_-]\.[A-Za-z0-9]{1,10})(?::|#L|,? lines? )(?<line>\d{1,7})", System.Text.RegularExpressions.RegexOptions.CultureInvariant, 1000)]
    private static partial System.Text.RegularExpressions.Regex CitationPattern();

    private static string Findings(int count) => count == 1 ? "1 finding" : string.Create(CultureInfo.InvariantCulture, $"{count} findings");

    private static string Cut(string text, int limit) => text.Length <= limit ? text : text[..limit];
}

/// <summary>The rules every note of a review's card keeps: its length, how a list of notes is capped, and how many a card holds.</summary>
public static class ReviewNotes
{
    /// <summary><paramref name="text"/> cut to <see cref="FindingLimits.MaxNoteLength"/>, ending with <c>...</c> when cut.</summary>
    public static string Cut(string text) =>
        text.Length <= FindingLimits.MaxNoteLength ? text : string.Concat(text.AsSpan(0, FindingLimits.MaxNoteLength - 3), "...");

    /// <summary>The first <paramref name="max"/> of <paramref name="notes"/> and, when there are more, the line <paramref name="closing"/> gives for how many more.</summary>
    public static IReadOnlyList<string> Capped(IReadOnlyList<string> notes, int max, Func<int, string> closing)
    {
        var more = notes.Count - max;
        return more <= 0 ? notes : [.. notes.Take(max), closing(more)];
    }

    /// <summary>
    /// <paramref name="notes"/> within <see cref="FindingLimits.MaxNotes"/>: when there are more, the first ones and a last
    /// line counting the notes not shown.
    /// </summary>
    public static IReadOnlyList<string> Fit(IReadOnlyList<string> notes) =>
        Capped(notes, notes.Count <= FindingLimits.MaxNotes ? FindingLimits.MaxNotes : FindingLimits.MaxNotes - 1, more =>
            string.Create(CultureInfo.InvariantCulture, $"{more} more notes are not shown."));
}
