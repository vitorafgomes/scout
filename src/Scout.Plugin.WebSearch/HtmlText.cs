using System.Net;
using System.Text.RegularExpressions;

namespace Scout.Plugin.WebSearch;

/// <summary>
/// Best-effort conversion of an HTML document to readable plain text: scripts,
/// styles, comments, and tags are removed, block boundaries become line breaks,
/// HTML entities are decoded, and runs of whitespace are collapsed. The compiled
/// patterns are non-backtracking-friendly and callers cap the input length, so
/// this stays cheap on hostile input.
/// </summary>
internal static partial class HtmlText
{
    [GeneratedRegex(
        "<(script|style)\\b[^>]*>.*?</(script|style)>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(
        "</(p|div|li|ul|ol|tr|table|thead|tbody|h[1-6]|section|article|header|footer|blockquote|pre)>",
        RegexOptions.IgnoreCase)]
    private static partial Regex BlockClose();

    [GeneratedRegex("<br\\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTag();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex("[ \\t\\r\\f\\v]+")]
    private static partial Regex InlineWhitespace();

    /// <summary>Strips HTML markup from <paramref name="html"/> and returns readable text.</summary>
    /// <param name="html">Raw HTML to convert.</param>
    /// <returns>The readable text, with collapsed whitespace.</returns>
    public static string ToReadable(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        var text = ScriptOrStyle().Replace(html, " ");
        text = Comment().Replace(text, " ");
        text = BlockClose().Replace(text, "\n");
        text = LineBreakTag().Replace(text, "\n");
        text = AnyTag().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        return CollapseWhitespace(text);
    }

    /// <summary>Collapses inline whitespace and blank lines in already-plain text.</summary>
    /// <param name="text">The text to normalise.</param>
    /// <returns>The text with single spaces and no empty lines.</returns>
    public static string CollapseWhitespace(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var lines = text.Split('\n')
            .Select(line => InlineWhitespace().Replace(line, " ").Trim())
            .Where(line => line.Length > 0);
        return string.Join("\n", lines);
    }
}
