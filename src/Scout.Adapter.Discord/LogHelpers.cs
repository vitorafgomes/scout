namespace Scout.Adapter.Discord;

/// <summary>
/// Small utilities shared by the adapter's observability logs. Lives inside
/// the adapter because it is shaped for Discord's truncation budget rather
/// than a generic formatter.
/// </summary>
internal static class LogHelpers
{
    /// <summary>
    /// Maximum number of characters of user content emitted in log lines.
    /// Keeps long messages from inflating logs and reduces PII surface.
    /// </summary>
    public const int MaxLoggedContentLength = 80;

    /// <summary>
    /// Truncate <paramref name="content"/> to <see cref="MaxLoggedContentLength"/>,
    /// appending an ellipsis marker when truncation occurred. Null input is
    /// rendered as an empty string.
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
        return content[..MaxLoggedContentLength] + "...";
    }
}
