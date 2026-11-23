using System.Text.RegularExpressions;

namespace Scout.Review;

/// <summary>One request the description states, with its label (<c>R1</c>) when it has one, and its text as written.</summary>
/// <param name="Label">The request's label, such as <c>R1</c>, or null when the author gave none.</param>
/// <param name="Text">What was asked, as the author wrote it: data, never an instruction.</param>
public sealed record DescriptionRequest(string? Label, string Text);

/// <summary>One section of a description: what the reader can show of it, and nothing it cannot.</summary>
/// <param name="Title">The heading's text without a leading emoji shortcode; empty for a description with no heading.</param>
/// <param name="Present">Whether the description has this section at all.</param>
/// <param name="Filled">
/// Whether the section holds something of the author's own: a line that is not the template's, not blank, not a comment,
/// not an unticked box, and not only a list marker, a label or an empty table row.
/// </param>
/// <param name="BoxesTicked">The ticked boxes of the section.</param>
/// <param name="BoxesTotal">The boxes of the section, ticked or not.</param>
/// <param name="Ticked">The text of each ticked box, as written.</param>
/// <param name="TableRows">The rows of the section's tables the author filled, the header and the template's rows left out.</param>
/// <param name="RequestsNamed">The request labels the author's own lines of the section name, in the order they first appear.</param>
public sealed record DescriptionSection(
    string Title,
    bool Present,
    bool Filled,
    int BoxesTicked,
    int BoxesTotal,
    IReadOnlyList<string> Ticked,
    int TableRows,
    IReadOnlyList<string> RequestsNamed);

/// <summary>
/// What the description of a pull request gives, read by code: its sections, which are filled, what was asked request by
/// request, and which requests no test names. Every text it holds is the author's, data from the pull request: nothing
/// here is an instruction to Scout, and nothing is rendered or interpreted. The description is read up to
/// <see cref="MaxLength"/> characters, at most <see cref="MaxSections"/> sections and <see cref="MaxRequests"/> requests
/// are kept, and the rest is counted.
/// </summary>
/// <param name="Sections">The template's sections when a template is given, else the description's own; one unnamed section when it has no heading.</param>
/// <param name="SectionsLeftOut">The sections past <see cref="MaxSections"/>.</param>
/// <param name="Requests">The entries of the section that states what was asked.</param>
/// <param name="RequestsLeftOut">The requests past <see cref="MaxRequests"/>.</param>
/// <param name="RequestsWithoutTest">
/// The labels of the requests the section of the tests implemented does not name; null when the description has no such
/// section or no labelled request, so nothing is said of tests it does not show.
/// </param>
/// <param name="Cut">Whether the description was longer than <see cref="MaxLength"/>.</param>
public sealed partial record PullRequestDescription(
    IReadOnlyList<DescriptionSection> Sections,
    int SectionsLeftOut,
    IReadOnlyList<DescriptionRequest> Requests,
    int RequestsLeftOut,
    IReadOnlyList<string>? RequestsWithoutTest,
    bool Cut)
{
    /// <summary>The characters of a description read: the review job keeps the description at this length too.</summary>
    public const int MaxLength = 16_000;

    /// <summary>The sections kept.</summary>
    public const int MaxSections = 30;

    /// <summary>The requests kept.</summary>
    public const int MaxRequests = 50;

    /// <summary>The title of the section that states what was asked.</summary>
    public const string AskedTitle = "What was asked";

    /// <summary>The title of the section that lists the tests implemented.</summary>
    public const string TestsTitle = "Tests implemented";

    /// <summary>The sections filled.</summary>
    public int SectionsFilled => Sections.Count(s => s.Filled);

    [GeneratedRegex(@"<!--.*?(?:-->|\z)", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(@"\A {0,3}##[ \t]+(.*)\z")]
    private static partial Regex Heading();

    [GeneratedRegex(@"\A:[a-z0-9_+-]+:[ \t]*")]
    private static partial Regex Shortcode();

    [GeneratedRegex(@"\A {0,3}(```|~~~)")]
    private static partial Regex Fence();

    [GeneratedRegex(@"\A\s*(?:[-*+]|\d+[.)])\s+\[( |x|X)\][ \t]*(.*)\z")]
    private static partial Regex Box();

    [GeneratedRegex(@"\A\|?\s*:?-{3,}:?\s*(?:\|\s*:?-{3,}:?\s*)*\|?\z")]
    private static partial Regex TableSeparator();

    [GeneratedRegex(@"\A\s{0,1}(?:[-*+]|\d+[.)])\s+(.*)\z")]
    private static partial Regex TopItem();

    [GeneratedRegex(@"\A(?:\*\*(R\d{1,3})\*\*[ \t]*:?|(R\d{1,3})(?=[:\s]|\z):?)[ \t]*(.*)\z")]
    private static partial Regex Labelled();

    [GeneratedRegex(@"\bR\d{1,3}\b")]
    private static partial Regex Label();

    [GeneratedRegex(@"\A\s*(?:[-*+]|\d+[.)])\s+(?:\[[ xX]\]\s*)?")]
    private static partial Regex Marker();

    [GeneratedRegex(@"\A(?:\*\*[^*]{1,30}\*\*|R\d{1,3}(?=[:\s]|\z))[ \t]*:?")]
    private static partial Regex LeadingLabel();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>
    /// Reads <paramref name="description"/> by the level-2 headings of <paramref name="template"/>, the repository's pull
    /// request template, or by its own when no template is given.
    /// </summary>
    public static PullRequestDescription Read(string? description, string? template = null)
    {
        var text = (description ?? string.Empty).ReplaceLineEndings("\n");
        var cut = text.Length > MaxLength;
        var own = Parse(cut ? text[..MaxLength] : text);
        var templated = string.IsNullOrWhiteSpace(template) ? null : Parse(template.ReplaceLineEndings("\n"));

        List<DescriptionSection> all;
        if (templated is not null)
        {
            var titled = templated.Where(s => s.Title is not null).ToList();
            all = [.. titled.Select(t => Describe(t.Title!, own.FirstOrDefault(s => Same(s.Title, t.Title)), t))];
        }
        else
        {
            var titled = own.Where(s => s.Title is not null).ToList();
            all = titled.Count > 0
                ? [.. titled.Select(s => Describe(s.Title!, s, null))]
                : [Describe(string.Empty, own[0], null)];
        }

        var asked = own.FirstOrDefault(s => Same(s.Title, AskedTitle)) is { } block
            ? RequestsOf(block, templated?.FirstOrDefault(s => Same(s.Title, AskedTitle)))
            : [];
        var labels = asked.Where(r => r.Label is not null).Select(r => r.Label!).Distinct(StringComparer.Ordinal).ToList();
        var tests = all.FirstOrDefault(s => Same(s.Title, TestsTitle) && s.Present);
        IReadOnlyList<string>? withoutTest = tests is null || labels.Count == 0
            ? null
            : [.. labels.Where(l => !tests.RequestsNamed.Contains(l, StringComparer.Ordinal))];

        return new PullRequestDescription(
            [.. all.Take(MaxSections)],
            Math.Max(0, all.Count - MaxSections),
            [.. asked.Take(MaxRequests)],
            Math.Max(0, asked.Count - MaxRequests),
            withoutTest,
            cut);
    }

    private sealed record Line(string Text, bool InFence, bool IsFence);

    private sealed record Block(string? Title, List<Line> Lines);

    /// <summary>The blocks of a text: the lines before the first level-2 heading, then one block per heading, comments removed and fences kept as text.</summary>
    private static List<Block> Parse(string text)
    {
        var blocks = new List<Block> { new(null, []) };
        string? fence = null;
        foreach (var raw in Comment().Replace(text, string.Empty).Split('\n'))
        {
            var opener = Fence().Match(raw);
            if (opener.Success && (fence is null || opener.Groups[1].Value == fence))
            {
                fence = fence is null ? opener.Groups[1].Value : null;
                blocks[^1].Lines.Add(new Line(raw, true, true));
                continue;
            }

            if (fence is null && Heading().Match(raw) is { Success: true } heading)
            {
                var title = heading.Groups[1].Value.TrimEnd(' ', '\t').TrimEnd('#').TrimEnd(' ', '\t');
                blocks.Add(new Block(Shortcode().Replace(title, string.Empty).Trim(), []));
                continue;
            }

            blocks[^1].Lines.Add(new Line(raw, fence is not null, false));
        }

        return blocks;
    }

    private static bool Same(string? title, string? other) =>
        title is not null && other is not null && string.Equals(title.Trim(), other.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Normal(string line) => Spaces().Replace(line, " ").Trim();

    private static HashSet<string> TemplateLines(Block? template) =>
        new(template?.Lines.Select(l => Normal(l.Text)) ?? [], StringComparer.Ordinal);

    /// <summary>Whether <paramref name="line"/> says anything once its list marker, its box, a leading label and the table's pipes are taken away.</summary>
    private static bool HasContent(string line)
    {
        var rest = Marker().Replace(line, string.Empty).Trim();
        rest = LeadingLabel().Replace(rest, string.Empty).Replace("|", string.Empty, StringComparison.Ordinal);
        return rest.Any(char.IsLetterOrDigit);
    }

    private static DescriptionSection Describe(string title, Block? block, Block? template)
    {
        var known = TemplateLines(template);
        var (filled, ticked, total, rows) = (false, 0, 0, 0);
        var tickedTexts = new List<string>();
        var named = new List<string>();
        var afterSeparator = false;
        foreach (var line in block?.Lines ?? [])
        {
            var mine = !known.Contains(Normal(line.Text));
            if (line.IsFence || string.IsNullOrWhiteSpace(line.Text))
            {
                afterSeparator &= !string.IsNullOrWhiteSpace(line.Text);
                continue;
            }

            if (line.InFence)
            {
                filled |= mine && line.Text.Any(char.IsLetterOrDigit);
                continue;
            }

            var box = Box().Match(line.Text);
            if (box.Success)
            {
                total++;
                if (box.Groups[1].Value != " ")
                {
                    ticked++;
                    tickedTexts.Add(box.Groups[2].Value.Trim());
                }
            }

            var table = line.Text.TrimStart().StartsWith('|');
            if (table && TableSeparator().IsMatch(line.Text.Trim()))
            {
                afterSeparator = true;
                continue;
            }

            if (table && afterSeparator && mine && HasContent(line.Text))
            {
                rows++;
            }

            afterSeparator &= table;
            if (!mine)
            {
                continue;
            }

            filled |= (!box.Success || box.Groups[1].Value != " ") && HasContent(line.Text);
            foreach (Match label in Label().Matches(line.Text))
            {
                if (!named.Contains(label.Value, StringComparer.Ordinal))
                {
                    named.Add(label.Value);
                }
            }
        }

        return new DescriptionSection(title, block is not null, filled, ticked, total, tickedTexts, rows, named);
    }

    /// <summary>The top-level list items of the section that states what was asked, the template's own and the bare labels left out.</summary>
    private static List<DescriptionRequest> RequestsOf(Block block, Block? template)
    {
        var known = TemplateLines(template);
        var requests = new List<DescriptionRequest>();
        foreach (var line in block.Lines)
        {
            if (line.InFence || line.IsFence || known.Contains(Normal(line.Text)) || TopItem().Match(line.Text) is not { Success: true } item)
            {
                continue;
            }

            var entry = item.Groups[1].Value.Trim();
            var labelled = Labelled().Match(entry);
            var label = labelled.Success ? (labelled.Groups[1].Success ? labelled.Groups[1].Value : labelled.Groups[2].Value) : null;
            var text = labelled.Success ? labelled.Groups[3].Value.Trim() : entry;
            if (text.Length > 0)
            {
                requests.Add(new DescriptionRequest(label, text));
            }
        }

        return requests;
    }
}
