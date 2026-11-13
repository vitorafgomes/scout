using System.Globalization;
using System.Text.RegularExpressions;

namespace Scout.Plugin.GitHub;

/// <summary>
/// Reads a unified diff the way GitHub anchors review comments: a comment on the right side may sit on any
/// line the new file has inside a hunk, context or added; a removed line belongs to the left side and is not
/// one of them. An empty line is not a diff line (the diff spells an empty context line as one space), a
/// "no newline" marker counts nothing, and any other text ends the hunk.
/// </summary>
internal static partial class DiffLines
{
    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@")]
    private static partial Regex HunkHeader();

    /// <summary>The line numbers of the new file a review comment may be anchored to, from <paramref name="patch"/>.</summary>
    internal static HashSet<int> RightSideLines(string? patch)
    {
        var lines = new HashSet<int>();
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
                    lines.Add(next);
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
