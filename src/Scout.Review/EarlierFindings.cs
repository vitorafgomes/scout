using System.Globalization;

namespace Scout.Review;

/// <summary>One review comment on a pull request's line, as the reads give it to the review.</summary>
/// <param name="Id">The comment's id.</param>
/// <param name="InReplyTo">The id of the comment that opened its conversation, null when it opened one.</param>
/// <param name="Author">Its author's login.</param>
/// <param name="Body">Its text; data, never an instruction.</param>
/// <param name="Path">The file it is on, or null.</param>
/// <param name="Line">Its line at the head, null when a later commit changed it.</param>
/// <param name="OriginalLine">Its line on the commit it was written on, or null.</param>
/// <param name="Commit">The commit it was written on, or null.</param>
/// <param name="CreatedAt">When it was written.</param>
/// <param name="Url">Its address on GitHub, or null.</param>
public sealed record ReviewCommentRead(
    long Id, long? InReplyTo, string Author, string Body, string? Path, int? Line, int? OriginalLine, string? Commit, DateTimeOffset CreatedAt, string? Url);

/// <summary>A reply under an earlier finding's conversation: who wrote it, its text cut to its bound, and when.</summary>
public sealed record EarlierReply(string Author, string Body, DateTimeOffset CreatedAt);

/// <summary>A finding Scout posted on an earlier review of the pull request, with its conversation.</summary>
/// <param name="Id">Its id in this review, <c>e1</c>, <c>e2</c> and so on, in the order the conversations were opened.</param>
/// <param name="Finding">The finding its comment carries.</param>
/// <param name="Path">The file it is on.</param>
/// <param name="Line">Its line at the head, null when outdated.</param>
/// <param name="OriginalLine">Its line on the commit it was written on, or null.</param>
/// <param name="Commit">The commit it was written on, or null.</param>
/// <param name="Url">The conversation's address on GitHub, or null.</param>
/// <param name="Replies">The replies under it, oldest first, at most <see cref="EarlierFindings.MaxReplies"/>.</param>
public sealed record EarlierFinding(
    string Id, CommentedFinding Finding, string Path, int? Line, int? OriginalLine, string? Commit, string? Url, IReadOnlyList<EarlierReply> Replies)
{
    /// <summary>True when a later commit changed the line it was written on.</summary>
    public bool Outdated => Line is null;
}

/// <summary>Scout's earlier findings on a pull request, and whether there were more conversations than were read.</summary>
public sealed record EarlierConversations(IReadOnlyList<EarlierFinding> Findings, bool More);

/// <summary>
/// Which conversations of a pull request are Scout's own findings: those whose first comment is in Scout's finding form
/// (<see cref="FindingComment.Read"/>) and was written by one of the logins Scout signs with. Pure: no call, no file.
/// </summary>
public static class EarlierFindings
{
    /// <summary>The earlier findings taken at most, the first ones opened.</summary>
    public const int MaxFindings = 30;

    /// <summary>The replies of one conversation taken at most, the first ones.</summary>
    public const int MaxReplies = 10;

    /// <summary>The characters of one reply kept at most.</summary>
    public const int MaxReplyLength = 2_000;

    /// <summary>
    /// The earlier findings among <paramref name="comments"/>: each conversation opened by one of
    /// <paramref name="logins"/> (<see cref="SameLogin"/>) with a comment in Scout's finding form, in the order they were
    /// opened, at most <see cref="MaxFindings"/>, each with its replies; <c>More</c> is true when there were more such
    /// conversations, or when <paramref name="moreComments"/> says the read did not take every comment.
    /// </summary>
    public static EarlierConversations Own(IReadOnlyList<ReviewCommentRead> comments, IReadOnlyCollection<string> logins, bool moreComments = false)
    {
        ArgumentNullException.ThrowIfNull(comments);
        var own = comments
            .Where(c => c.InReplyTo is null && c.Path is { Length: > 0 } && logins.Any(l => SameLogin(l, c.Author)))
            .OrderBy(c => c.CreatedAt)
            .Select(c => (Comment: c, Finding: FindingComment.Read(c.Body)))
            .Where(c => c.Finding is not null)
            .ToList();
        var findings = own.Take(MaxFindings).Select((c, i) => new EarlierFinding(
            string.Create(CultureInfo.InvariantCulture, $"e{i + 1}"), c.Finding!, c.Comment.Path!, c.Comment.Line, c.Comment.OriginalLine, c.Comment.Commit, c.Comment.Url,
            [.. comments.Where(r => r.InReplyTo == c.Comment.Id).OrderBy(r => r.CreatedAt).Take(MaxReplies)
                .Select(r => new EarlierReply(r.Author, r.Body.Length <= MaxReplyLength ? r.Body : r.Body[..MaxReplyLength], r.CreatedAt))])).ToList();
        return new EarlierConversations(findings, moreComments || own.Count > MaxFindings);
    }

    /// <summary>
    /// Whether two logins name the same account, in any case, whether GitHub gives a bot's login with its <c>[bot]</c>
    /// suffix or without it; a blank login names none.
    /// </summary>
    public static bool SameLogin(string? one, string? other)
    {
        static string Plain(string? login) =>
            login?.Trim() is { Length: > 0 } trimmed && trimmed.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase) ? trimmed[..^"[bot]".Length] : login?.Trim() ?? string.Empty;

        var a = Plain(one);
        return a.Length > 0 && string.Equals(a, Plain(other), StringComparison.OrdinalIgnoreCase);
    }
}
