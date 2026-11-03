using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Scout.Operator;

/// <summary>
/// Text drawn into a video with ffmpeg's <c>drawtext</c> filter, with the font passed by path and text expansion off,
/// every value escaped for the filter option and again for the filter graph, so no text or path can change the graph.
/// </summary>
internal static partial class FilterText
{
    /// <summary>Escape <paramref name="value"/> as a filter option value inside a filter graph.</summary>
    internal static string Escape(string value)
    {
        var option = value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal);
        return option.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal);
    }

    /// <summary><paramref name="text"/> on one line: control characters become spaces and runs of spaces become one.</summary>
    internal static string Clean(string text)
        => Spaces().Replace(new string([.. text.Select(c => char.IsControl(c) ? ' ' : c)]), " ").Trim();

    /// <summary>The first sentence of <paramref name="text"/>, or all of it when it has one.</summary>
    internal static string FirstSentence(string text)
    {
        var clean = Clean(text);
        var end = SentenceEnd().Match(clean);
        return end.Success ? clean[..(end.Index + 1)] : clean;
    }

    /// <summary><paramref name="text"/> wrapped at spaces into lines of at most <paramref name="width"/> characters; a longer word stays whole.</summary>
    internal static IReadOnlyList<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        var line = new StringBuilder();
        foreach (var word in Clean(text).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                lines.Add(line.ToString());
                line.Clear();
            }

            line.Append(line.Length > 0 ? " " : string.Empty).Append(word);
        }

        if (line.Length > 0)
        {
            lines.Add(line.ToString());
        }

        return lines;
    }

    /// <summary>
    /// One <c>drawtext</c> filter: <paramref name="text"/> in white at <paramref name="size"/>, centred across, at
    /// <paramref name="y"/>, on a dark box when <paramref name="boxed"/>, shown from <paramref name="fromMs"/> to
    /// <paramref name="toMs"/> when both are given.
    /// </summary>
    internal static string Draw(string fontPath, string text, int size, int y, bool boxed, int? fromMs = null, int? toMs = null)
    {
        var filter = new StringBuilder("drawtext=fontfile=")
            .Append(Escape(fontPath))
            .Append(":expansion=none:text=")
            .Append(Escape(text))
            .Append(CultureInfo.InvariantCulture, $":fontsize={size}:fontcolor=white:x=(w-text_w)/2:y={y}");
        if (boxed)
        {
            filter.Append(":box=1:boxcolor=black@0.6:boxborderw=16");
        }

        if (fromMs is { } from && toMs is { } to)
        {
            filter.Append(":enable=").Append(Escape($"between(t,{FfmpegToolset.Seconds(from)},{FfmpegToolset.Seconds(to)})"));
        }

        return filter.ToString();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"[.!?](?=\s|$)")]
    private static partial Regex SentenceEnd();
}
