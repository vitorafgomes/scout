using System.Globalization;
using System.Text.RegularExpressions;

namespace Scout.Review;

/// <summary>
/// Reads a unified diff the way GitHub anchors review comments: a comment on the right side may sit on any
/// line the new file has inside a hunk, context or added; a removed line belongs to the left side and is not
/// one of them. An empty line is not a diff line (the diff spells an empty context line as one space), a
/// "no newline" marker counts nothing, and any other text ends the hunk.
/// </summary>
public static partial class DiffLines
{
    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@")]
    internal static partial Regex HunkHeader();

    /// <summary>The lines of a hunk shown on each side of the finding's line at most, removed lines counted.</summary>
    public const int HunkWindow = 30;

    /// <summary>
    /// The hunk of <paramref name="patch"/> whose new side holds <paramref name="line"/>, with its header, its removed and its
    /// added lines as the diff writes them; when no hunk's new side holds it, the hunk with no new line whose new start is
    /// that line (where a removal with nothing after it is placed), else the first hunk (a deleted file is anchored at line 1).
    /// A hunk longer than twice <see cref="HunkWindow"/> lines keeps the window around the hunk line that holds the first
    /// line of code of <paramref name="quote"/>, when one does, else around the line, and says how many lines it left out.
    /// Null when the patch holds no hunk.
    /// </summary>
    public static string? HunkHolding(string? patch, int line, string? quote = null)
    {
        var hunks = new List<(List<string> Lines, int At)>();
        var next = 0;
        foreach (var raw in (patch ?? string.Empty).Split('\n'))
        {
            var text = raw.TrimEnd('\r');
            var header = HunkHeader().Match(text);
            if (header.Success)
            {
                next = int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture);
                hunks.Add(([text], -1));
                continue;
            }

            if (hunks.Count == 0 || text.Length == 0 || text[0] is not ('+' or '-' or ' ' or '\\'))
            {
                continue;
            }

            var (lines, at) = hunks[^1];
            lines.Add(text);
            if (text[0] is '+' or ' ')
            {
                if (next == line)
                {
                    hunks[^1] = (lines, lines.Count - 1);
                }

                next++;
            }
        }

        if (hunks.Count == 0)
        {
            return null;
        }

        var (held, index) = hunks.FirstOrDefault(h => h.At >= 0) is { Lines: not null } found
            ? found
            : (hunks.FirstOrDefault(h => int.Parse(HunkHeader().Match(h.Lines[0]).Groups[1].Value, CultureInfo.InvariantCulture) is var start && Math.Max(1, start) == line && !h.Lines.Skip(1).Any(l => l[0] is '+' or ' ')).Lines ?? hunks[0].Lines, 1);
        if (held.Count <= (2 * HunkWindow) + 1)
        {
            return string.Join('\n', held);
        }

        var quoted = quote?.ReplaceLineEndings("\n").Split('\n').FirstOrDefault(DiffQuote.IsCode) is { } code ? DiffQuote.Normal(code) : null;
        var quotedAt = quoted is null ? -1 : held.FindIndex(1, l => DiffQuote.Normal(l[1..]).Contains(quoted, StringComparison.Ordinal));
        index = quotedAt > 0 ? quotedAt : index;
        var from = Math.Max(1, index - HunkWindow);
        var to = Math.Min(held.Count - 1, index + HunkWindow);
        var left = held.Count - 1 - (to - from + 1);
        return string.Join('\n', held.Take(1).Concat(held.Skip(from).Take(to - from + 1)))
            + string.Create(CultureInfo.InvariantCulture, $"\n({left} more lines of this hunk are not shown.)");
    }

    /// <summary>The line numbers of the new file a review comment may be anchored to, from <paramref name="patch"/>.</summary>
    public static HashSet<int> RightSideLines(string? patch) => [.. RightSide(patch).Select(l => l.Line)];

    /// <summary>
    /// The new side of <paramref name="patch"/>: each context or added line inside a hunk, its number in the new file and its
    /// text without the diff marker, in the patch's order; a line ending in a carriage return is read without it.
    /// </summary>
    public static IReadOnlyList<(int Line, string Text)> RightSide(string? patch)
    {
        var lines = new List<(int Line, string Text)>();
        if (string.IsNullOrEmpty(patch))
        {
            return lines;
        }

        var next = 0;
        var inHunk = false;
        foreach (var raw in patch.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var header = HunkHeader().Match(line);
            if (header.Success)
            {
                next = int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture);
                inHunk = true;
                continue;
            }

            if (!inHunk || line.Length == 0)
            {
                continue;
            }

            switch (line[0])
            {
                case '+':
                case ' ':
                    lines.Add((next, line[1..]));
                    next++;
                    break;
                case '-':
                    break;
                case '\\':
                    break;
                default:
                    inHunk = false;
                    break;
            }
        }

        return lines;
    }
}
