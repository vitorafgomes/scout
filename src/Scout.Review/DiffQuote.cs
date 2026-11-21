using System.Globalization;

namespace Scout.Review;

/// <summary>Where a quote sits in a patch: the line Scout derives from it, or the problem that keeps it from sitting anywhere.</summary>
/// <param name="Line">The new-file line of the quote's first line of code; null when the quote has a problem.</param>
/// <param name="Problem">Why the quote sits nowhere, worded to follow "it" in the batch's sentence; null when it sits on <paramref name="Line"/>.</param>
public sealed record QuoteMatch(int? Line, string? Problem);

/// <summary>
/// A finding's evidence: a quote of the new side of its file's patch, or of its removed lines, from which Scout derives the
/// finding's line: that of the quote's first line of code (<see cref="IsCode"/>), or, for removed lines, the new-file line
/// where the removal happened. Whitespace at the ends of a line and its width inside a line do not count; the quote must
/// sit in exactly one place (<see cref="Locate"/>).
/// </summary>
public static class DiffQuote
{
    /// <summary>The problem of a finding with no quote.</summary>
    public const string Missing = "has no evidence quoting its file's patch.";

    /// <summary>The problem of a quote that is neither on the new side of its file's patch nor among its removed lines.</summary>
    public const string NotFound = "has evidence not found in its file's patch.";

    /// <summary>The problem of a quote with no line of code in it.</summary>
    public const string NotCode = "has evidence that is only a blank line, a comment or a directive.";

    /// <summary>The problem of a quote that sits in more than one place.</summary>
    public static string FoundTimes(int count) =>
        string.Create(CultureInfo.InvariantCulture, $"has evidence found {count} times in its file's patch.");

    /// <summary>
    /// Where <paramref name="quote"/> sits in <paramref name="patch"/>. Each line is compared with its leading and trailing
    /// whitespace removed and each run of spaces or tabs inside it read as one space. The steps run in this order, each on
    /// the new side first and then on the removed lines, and the first that finds the quote decides: found once, it is
    /// placed; found more than once, it is refused with the count. 1, the quote's lines as adjacent lines. 2, the same with
    /// blank lines left out of the quote and of the patch. 3, on the removed lines only, the quote's lines in order inside
    /// one hunk, other lines between them. 4, the quote's first line of code alone, as a whole line and then as a part of
    /// one line. A quote no step finds is not found.
    /// </summary>
    public static QuoteMatch Locate(string? patch, string? quote)
    {
        if (string.IsNullOrWhiteSpace(quote))
        {
            return new QuoteMatch(null, Missing);
        }

        var wanted = quote.ReplaceLineEndings("\n").Split('\n');
        if (wanted.Length > 1 && wanted[^1].Length == 0)
        {
            wanted = wanted[..^1];
        }

        var code = Array.FindIndex(wanted, IsCode);
        var sought = wanted.Select(Normal).ToArray();
        var newSide = DiffLines.RightSide(patch).Select(l => new Entry(Normal(l.Text), l.Line, l.Line, 0)).ToList();
        var removed = RemovedLines(patch);
        var dense = sought.Where(t => t.Length > 0).ToArray();
        var denseCode = code < 0 ? -1 : sought.Take(code).Count(t => t.Length > 0);
        (string[] Sought, int CodeAt, Joins Joins, bool RemovedOnly)[] steps =
        [
            (sought, code, Joins.Adjacent, false),
            (dense, denseCode, Joins.OverBlanks, false),
            (dense, denseCode, Joins.InOneHunk, true),
        ];
        foreach (var step in steps)
        {
            foreach (var (side, isRemoved) in new[] { (newSide, false), (removed, true) })
            {
                var found = step.RemovedOnly && !isRemoved ? [] : Places(side, step.Sought, step.Joins);
                if (found.Count > 1)
                {
                    return new QuoteMatch(null, FoundTimes(found.Count));
                }

                if (found.Count == 1)
                {
                    return step.CodeAt < 0 ? new QuoteMatch(null, NotCode) : new QuoteMatch(side[found[0][step.CodeAt]].Line, null);
                }
            }
        }

        if (code < 0)
        {
            return new QuoteMatch(null, NotFound);
        }

        var alone = sought[code];
        foreach (var same in new Func<string, bool>[] { text => text == alone, text => text.Contains(alone, StringComparison.Ordinal) })
        {
            foreach (var side in new[] { newSide, removed })
            {
                var found = side.Where(e => same(e.Text)).ToList();
                if (found.Count > 1)
                {
                    return new QuoteMatch(null, FoundTimes(found.Count));
                }

                if (found.Count == 1)
                {
                    return new QuoteMatch(found[0].Line, null);
                }
            }
        }

        return new QuoteMatch(null, NotFound);
    }

    /// <summary>One line of a side of a patch: its normalised text, the line a finding on it sits on, its place in its side and its hunk.</summary>
    internal sealed record Entry(string Text, int Line, int At, int Hunk);

    private enum Joins
    {
        Adjacent,
        OverBlanks,
        InOneHunk,
    }

    /// <summary>A line with its leading and trailing whitespace removed and each run of spaces or tabs inside it read as one space.</summary>
    internal static string Normal(string line) =>
        string.Join(' ', line.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Each place <paramref name="sought"/> sits in <paramref name="side"/>, joined as <paramref name="joins"/> says: the indexes of the lines it takes.</summary>
    private static List<List<int>> Places(List<Entry> side, string[] sought, Joins joins)
    {
        var places = new List<List<int>>();
        for (var i = 0; sought.Length > 0 && i < side.Count; i++)
        {
            if (side[i].Text == sought[0] && Follows(side, i, sought, joins) is { } taken)
            {
                places.Add(taken);
            }
        }

        return places;
    }

    /// <summary>The indexes of the lines <paramref name="sought"/> takes from <paramref name="first"/> on, or null when it does not continue there.</summary>
    private static List<int>? Follows(List<Entry> side, int first, string[] sought, Joins joins)
    {
        var taken = new List<int> { first };
        for (var k = 1; k < sought.Length; k++)
        {
            var at = taken[^1];
            int? next = joins switch
            {
                Joins.Adjacent => at + 1 < side.Count && side[at + 1].At == side[at].At + 1 ? at + 1 : null,
                Joins.OverBlanks => NextOverBlanks(side, at),
                _ => Enumerable.Range(at + 1, side.Count - at - 1).Where(n => side[n].Hunk == side[first].Hunk && side[n].Text == sought[k]).Select(n => (int?)n).FirstOrDefault(),
            };
            if (next is not { } n || side[n].Text != sought[k])
            {
                return null;
            }

            taken.Add(n);
        }

        return taken;
    }

    private static int? NextOverBlanks(List<Entry> side, int at)
    {
        for (var n = at + 1; n < side.Count && side[n].At == side[n - 1].At + 1; n++)
        {
            if (side[n].Text.Length > 0)
            {
                return n;
            }
        }

        return null;
    }

    /// <summary>
    /// The removed lines inside the hunks of a patch, a file header before the first hunk not among them, each with the
    /// new-file line its removal happened at: the first new-side line after the
    /// removed run in the same hunk; when the hunk has none after it, the last new-side line before it; in a hunk with no
    /// new-side line at all, the hunk's new start line, at least 1.
    /// </summary>
    internal static List<Entry> RemovedLines(string? patch)
    {
        var removed = new List<Entry>();
        var lines = (patch ?? string.Empty).Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var hunkStart = -1;
        var newStart = 1;
        for (var i = 0; i <= lines.Length; i++)
        {
            var header = i < lines.Length ? DiffLines.HunkHeader().Match(lines[i]) : null;
            if (i == lines.Length || header!.Success)
            {
                if (hunkStart >= 0 && i > hunkStart)
                {
                    Derive(lines, hunkStart, i, newStart, removed);
                }

                if (header is { Success: true })
                {
                    newStart = int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture);
                    hunkStart = i + 1;
                }
            }
        }

        return removed;
    }

    private static void Derive(string[] lines, int from, int to, int newStart, List<Entry> removed)
    {
        var news = new List<(int At, int Line)>();
        var next = newStart;
        for (var i = from; i < to; i++)
        {
            if (lines[i].Length > 0 && lines[i][0] is '+' or ' ')
            {
                news.Add((i, next++));
            }
        }

        for (var i = from; i < to; i++)
        {
            if (lines[i].Length == 0 || lines[i][0] != '-')
            {
                continue;
            }

            var after = news.FirstOrDefault(n => n.At > i);
            var before = news.LastOrDefault(n => n.At < i);
            var line = after != default ? after.Line : before != default ? before.Line : Math.Max(1, newStart);
            removed.Add(new Entry(Normal(lines[i][1..]), line, i, from));
        }
    }

    /// <summary>
    /// Whether <paramref name="line"/> is code, by a small lexical rule and no parser, a heuristic: not blank; not only a
    /// comment, that is starting with <c>//</c>, <c>/*</c>, <c>*</c>, <c>#</c>, <c>&lt;!--</c> or <c>--</c>; and not an
    /// import directive: <c>using</c> or <c>global using</c> ending with a semicolon and holding no parenthesis and no
    /// <c>var</c>, <c>import</c> followed by a space or a brace, <c>from ... import</c>, or <c>package</c>. A directive in a
    /// <c>#</c> line, such as <c>#include</c>, is read as not code by the comment rule.
    /// </summary>
    public static bool IsCode(string line)
    {
        var text = line.Trim();
        if (text.Length == 0
            || text.StartsWith("//", StringComparison.Ordinal) || text.StartsWith("/*", StringComparison.Ordinal) || text.StartsWith('*')
            || text.StartsWith('#') || text.StartsWith("<!--", StringComparison.Ordinal) || text.StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        var usingDirective = (text.StartsWith("using ", StringComparison.Ordinal) || text.StartsWith("global using ", StringComparison.Ordinal))
            && text.EndsWith(';') && !text.Contains('(', StringComparison.Ordinal) && !text.StartsWith("using var ", StringComparison.Ordinal);
        var importDirective = text.StartsWith("import ", StringComparison.Ordinal) || text.StartsWith("import{", StringComparison.Ordinal)
            || (text.StartsWith("from ", StringComparison.Ordinal) && text.Contains(" import ", StringComparison.Ordinal))
            || text.StartsWith("package ", StringComparison.Ordinal);
        return !usingDirective && !importDirective;
    }
}
