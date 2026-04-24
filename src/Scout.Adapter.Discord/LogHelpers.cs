using System.Text;

namespace Scout.Adapter.Discord;

/// <summary>
/// Small utilities shared by the adapter's observability logs. Lives inside
/// the adapter because it is shaped for Discord's truncation budget rather
/// than a generic formatter.
/// </summary>
internal static class LogHelpers
{
    private const string Ellipsis = "...";

    /// <summary>
    /// Maximum number of characters of user content emitted in log lines.
    /// Keeps long messages from inflating logs and reduces PII surface.
    /// </summary>
    public const int MaxLoggedContentLength = 80;

    /// <summary>
    /// Truncate <paramref name="content"/> to <see cref="MaxLoggedContentLength"/>,
    /// appending an ellipsis marker when truncation occurred. Null input is
    /// rendered as an empty string. Truncation walks rune boundaries so
    /// surrogate pairs (e.g. emoji encoded as U+1F600) never split across
    /// the cut.
    /// </summary>
    public static string Truncate(string? content, out bool truncated)
    {
        if (string.IsNullOrEmpty(content))
        {
            truncated = false;
            return string.Empty;
        }

        if (content.Length <= MaxLoggedContentLength)
        {
            truncated = false;
            return content;
        }

        truncated = true;

        // Walk runes, accumulating into a buffer while leaving room for the
        // trailing ellipsis. Stop at the last rune whose UTF-16 length still
        // fits the budget, guaranteeing the cut never lands inside a
        // surrogate pair.
        var budget = MaxLoggedContentLength - Ellipsis.Length;
        if (budget <= 0)
        {
            return Ellipsis;
        }

        var builder = new StringBuilder(MaxLoggedContentLength);
        var used = 0;
        foreach (var rune in content.EnumerateRunes())
        {
            if (used + rune.Utf16SequenceLength > budget)
            {
                break;
            }
            builder.Append(rune.ToString());
            used += rune.Utf16SequenceLength;
        }

        builder.Append(Ellipsis);
        return builder.ToString();
    }
}
