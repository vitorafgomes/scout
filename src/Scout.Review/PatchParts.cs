using System.Globalization;
using System.Text.RegularExpressions;

namespace Scout.Review;

/// <summary>One part of a file's patch: a valid patch of the same file, or the one diff line that cannot fit, said.</summary>
/// <param name="Patch">The part as a patch, its hunk headers giving the whole patch's line numbers; null for a line that cannot fit.</param>
/// <param name="FirstLine">The first new-file line the part holds, or the line that cannot fit; 0 when it holds none.</param>
/// <param name="LastLine">The last new-file line the part holds; 0 when it holds none.</param>
/// <param name="Problem">Why the part cannot be read: a diff line longer than the bound; null otherwise.</param>
public sealed record PatchPart(string? Patch, int FirstLine, int LastLine, string? Problem = null);

/// <summary>
/// Splits one file's patch into parts that each fit a bound in characters: at hunk boundaries when it can, inside a hunk
/// by whole diff lines when one hunk alone is over the bound, each part a valid patch whose hunk headers give the same
/// old and new line numbers as the whole patch. The same patch and bound always give the same parts.
/// </summary>
public static partial class PatchParts
{
    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@(.*)$")]
    private static partial Regex Header();

    /// <summary>Why a patch over the bound with no hunk header cannot be split.</summary>
    public const string NoHunk = "the diff has no hunk header to split it at";

    private sealed record Line(string Text, int Old, int New);

    private sealed record Hunk(string Header, List<Line> Lines);

    /// <summary>The parts of <paramref name="patch"/>, each at most <paramref name="bound"/> characters, in the patch's order.</summary>
    public static IReadOnlyList<PatchPart> Split(string patch, int bound)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (patch.Length <= bound)
        {
            return [Part(patch)];
        }

        var parts = new List<PatchPart>();
        var pending = new List<string>();
        var pendingLength = 0;
        void Flush()
        {
            if (pending.Count > 0)
            {
                parts.Add(Part(string.Join('\n', pending)));
                (pending, pendingLength) = ([], 0);
            }
        }

        var hunks = Hunks(patch);
        if (hunks.Count == 0)
        {
            return [new PatchPart(null, 0, 0, NoHunk)];
        }

        foreach (var hunk in hunks)
        {
            var whole = string.Join('\n', [hunk.Header, .. hunk.Lines.Select(l => l.Text)]);
            if (whole.Length <= bound)
            {
                if (pendingLength + whole.Length + (pending.Count > 0 ? 1 : 0) > bound)
                {
                    Flush();
                }

                pending.Add(whole);
                pendingLength += whole.Length + (pending.Count > 1 ? 1 : 0);
                continue;
            }

            Flush();
            parts.AddRange(Inside(hunk, bound));
        }

        Flush();
        return parts;
    }

    /// <summary>A hunk over the bound, cut by whole diff lines, each piece under its own regenerated header.</summary>
    private static List<PatchPart> Inside(Hunk hunk, int bound)
    {
        var parts = new List<PatchPart>();
        var piece = new List<Line>();
        foreach (var line in Units(hunk.Lines))
        {
            var candidate = piece.Concat(line).ToList();
            if (Text(candidate).Length <= bound)
            {
                piece = candidate;
                continue;
            }

            if (piece.Count > 0)
            {
                parts.Add(Part(Text(piece)));
            }

            if (Text(line).Length > bound)
            {
                var at = line[0].Text.StartsWith('-') ? line[0].Old : line[0].New;
                parts.Add(new PatchPart(null, at, at, string.Create(
                    CultureInfo.InvariantCulture, $"line {at} of the diff is {line[0].Text.Length:N0} characters, more than a batch holds")));
                piece = [];
            }
            else
            {
                piece = line;
            }
        }

        if (piece.Count > 0)
        {
            parts.Add(Part(Text(piece)));
        }

        return parts;
    }

    /// <summary>The diff lines in units that stay together: a line with the "no newline" marker that follows it.</summary>
    private static IEnumerable<List<Line>> Units(List<Line> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var unit = new List<Line> { lines[i] };
            while (i + 1 < lines.Count && lines[i + 1].Text.StartsWith('\\'))
            {
                unit.Add(lines[++i]);
            }

            yield return unit;
        }
    }

    /// <summary>Lines of one hunk under a header that gives their old and new start lines and counts.</summary>
    private static string Text(List<Line> lines)
    {
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var olds = lines.Count(l => l.Text.Length > 0 && l.Text[0] is ' ' or '-');
        var news = lines.Count(l => l.Text.Length > 0 && l.Text[0] is ' ' or '+');
        var old = olds == 0 ? Math.Max(0, lines[0].Old - 1) : lines[0].Old;
        var @new = news == 0 ? Math.Max(0, lines[0].New - 1) : lines[0].New;
        var header = string.Create(CultureInfo.InvariantCulture, $"@@ -{old},{olds} +{@new},{news} @@");
        return string.Join('\n', [header, .. lines.Select(l => l.Text)]);
    }

    private static List<Hunk> Hunks(string patch)
    {
        var hunks = new List<Hunk>();
        int old = 0, @new = 0;
        foreach (var raw in patch.Split('\n'))
        {
            var header = Header().Match(raw.TrimEnd('\r'));
            if (header.Success)
            {
                old = int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture);
                @new = int.Parse(header.Groups[3].Value, CultureInfo.InvariantCulture);
                hunks.Add(new Hunk(raw, []));
                continue;
            }

            if (hunks.Count == 0)
            {
                continue;
            }

            hunks[^1].Lines.Add(new Line(raw, old, @new));
            switch (raw.Length > 0 ? raw[0] : '\0')
            {
                case ' ':
                    old++;
                    @new++;
                    break;
                case '-':
                    old++;
                    break;
                case '+':
                    @new++;
                    break;
            }
        }

        return hunks;
    }

    private static PatchPart Part(string patch)
    {
        var right = DiffLines.RightSide(patch);
        return right.Count == 0 ? new PatchPart(patch, 0, 0) : new PatchPart(patch, right[0].Line, right[^1].Line);
    }
}
