namespace Scout.Adapter.Discord.Streaming;

/// <summary>
/// Splits a reply into Discord-sized parts. A part ends at a paragraph, line or
/// space, never inside a word; a code fence is never split, unless the fence
/// alone is longer than a part, in which case the part closes it and the next
/// part reopens it with the same opener. At most <c>maxParts</c> parts are
/// returned; the last one then ends with <see cref="TruncatedMarker"/>.
/// </summary>
internal static class DiscordReplySplitter
{
    internal const string TruncatedMarker = "\n\n(reply truncated)";
    private const string Fence = "```";
    private const string FenceClose = "\n```";

    /// <summary>
    /// Each returned part is at most <paramref name="partLength"/> characters plus
    /// a closing fence (4) and, on the last part, <see cref="TruncatedMarker"/>.
    /// </summary>
    public static IReadOnlyList<string> Split(string text, int partLength, int maxParts)
    {
        var parts = new List<string>();
        var reopen = string.Empty;
        var start = 0;
        while (start < text.Length)
        {
            if (parts.Count == maxParts)
            {
                parts[^1] += TruncatedMarker;
                break;
            }

            var budget = partLength - reopen.Length;
            if (text.Length - start <= budget)
            {
                parts.Add(reopen + text[start..]);
                break;
            }

            var window = text.Substring(start, budget);
            var cut = CutPoint(window);
            var (insideFence, openerAt, opener) = FenceStateAt(window, cut, reopen);

            if (!insideFence)
            {
                parts.Add((reopen + window[..cut]).TrimEnd());
                reopen = string.Empty;
            }
            else if (openerAt > 0)
            {
                cut = openerAt;
                parts.Add((reopen + window[..cut]).TrimEnd());
                reopen = string.Empty;
            }
            else
            {
                parts.Add((reopen + window[..cut]).TrimEnd('\n') + FenceClose);
                reopen = (opener.Length < partLength / 4 ? opener : Fence) + "\n";
            }

            start += cut;
            while (start < text.Length && text[start] == '\n')
            {
                start++;
            }
        }
        return parts;
    }

    private static int CutPoint(string window)
    {
        var half = window.Length / 2;
        foreach (var separator in new[] { "\n\n", "\n" })
        {
            var at = window.LastIndexOf(separator, StringComparison.Ordinal);
            if (at >= half)
            {
                return at + separator.Length;
            }
        }
        var space = window.LastIndexOf(' ');
        if (space > 0)
        {
            return space + 1;
        }
        var newline = window.LastIndexOf('\n');
        return newline > 0 ? newline + 1 : window.Length;
    }

    private static (bool Inside, int OpenerAt, string Opener) FenceStateAt(string window, int cut, string reopen)
    {
        var inside = reopen.Length > 0;
        var opener = inside ? reopen.TrimEnd('\n') : string.Empty;
        var openerAt = inside ? 0 : -1;
        var lineStart = 0;
        while (lineStart < cut)
        {
            var lineEnd = window.IndexOf('\n', lineStart);
            if (lineEnd < 0)
            {
                lineEnd = window.Length;
            }
            var line = window[lineStart..lineEnd].TrimStart();
            if (line.StartsWith(Fence, StringComparison.Ordinal) && lineStart < cut)
            {
                inside = !inside;
                if (inside)
                {
                    opener = line.TrimEnd();
                    openerAt = lineStart;
                }
            }
            lineStart = lineEnd + 1;
        }
        return (inside, openerAt, opener);
    }
}
