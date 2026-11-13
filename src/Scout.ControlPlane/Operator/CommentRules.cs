using System.Text.RegularExpressions;

namespace Scout.ControlPlane.Operator;

/// <summary>
/// The comment vocabulary and what the worker may change in the stored comments: it adds a comment as new,
/// quarantined or drafted, sets the kind, theme and draft of one it has not handed to the owner, moves new or
/// draft-requested to drafted and new to quarantined, runs the execution steps of an approved reply or hold, adds
/// proposed ideas and reports its sync. It never drops a comment, changes what YouTube said, changes the owner's
/// approval or decision, or decides an idea.
/// </summary>
internal static partial class CommentRules
{
    public const string TargetPrefix = "comment:";
    public const string Reply = "comments.insert";
    public const string Hold = "comments.setModerationStatus";
    public const string New = "new";
    public const string DraftRequested = "draft_requested";
    public const string Drafted = "drafted";
    public const string ReplyApproved = "reply_approved";
    public const string Replying = "replying";
    public const string Replied = "replied";
    public const string Quarantined = "quarantined";
    public const string HoldApproved = "hold_approved";
    public const string Held = "held";
    public const string Dismissed = "dismissed";
    public const string Failed = "failed";
    public const string Spam = "spam";
    public const string Other = "other";
    public const string Proposed = "proposed";
    public const string Used = "used";
    public const int MaxComments = 1000;
    public const int MaxIdeas = 200;
    public const string LeftReplying =
        "The reply may already be on YouTube; check the video's comments in YouTube Studio before replying again.";

    private const int MaxText = 2000;
    private const int MaxDraft = 1000;
    private const int MaxTheme = 40;
    private const int MaxIdeaText = 200;

    private static readonly string[] Kinds = ["question", "request", "praise", "feedback", Other, Spam];
    private static readonly string[] Statuses =
        [New, DraftRequested, Drafted, ReplyApproved, Replying, Replied, Quarantined, HoldApproved, Held, Dismissed, Failed];
    private static readonly string[] AddedStatuses = [New, Quarantined, Drafted];
    private static readonly string[] WorkerStatuses = [New, DraftRequested, Drafted, Quarantined];
    private static readonly string[] IdeaStatuses = [Proposed, Used, Dismissed];

    public static string TargetOf(string commentId) => TargetPrefix + commentId;

    /// <summary>The worker's step of an approved reply or hold: the only change it may make to such a comment.</summary>
    public static bool IsExecution(string from, string to) =>
        (from, to) is (ReplyApproved, Replying) or (Replying, Replied) or (Replying or ReplyApproved, Failed)
            or (HoldApproved, Held) or (HoldApproved, Failed);

    /// <summary>
    /// The state after a worker save: the worker's comments merged over the stored ones, each keeping its
    /// stored fetch time, the owner's ideas unchanged, the sync request cleared by a sync at or after it or by a failed
    /// sync, one with an error and no newer sync time, the stored error kept by a save without one and cleared by a
    /// newer sync time, and the caps, which never drop a comment whose target is in
    /// <paramref name="live"/>, the comments with a live approved or executing action.
    /// </summary>
    /// <exception cref="OperatorValidationException">A kind, status, id or length outside what the operator accepts.</exception>
    /// <exception cref="OperatorTamperingException">The worker changes what only the owner or YouTube sets.</exception>
    public static CommentsState MergeFromWorker(CommentsState stored, CommentsState incoming, DateTimeOffset now, IReadOnlySet<string> live)
    {
        Validate(incoming);
        var before = stored.Comments.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var sent = incoming.Comments.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        if (stored.Comments.FirstOrDefault(c => !sent.Contains(c.Id)) is { } dropped)
        {
            throw new OperatorTamperingException($"The worker may not drop comment '{dropped.Id}'.");
        }

        var comments = incoming.Comments
            .Select(c => before.TryGetValue(c.Id, out var stale) ? Merge(stale, c) : Added(c, now))
            .ToList();
        var syncedAt = incoming.SyncedAt ?? stored.SyncedAt;
        var newer = incoming.SyncedAt is { } reportedAt && (stored.SyncedAt is not { } last || reportedAt > last);
        var reported = string.IsNullOrEmpty(incoming.SyncError) ? null : incoming.SyncError;
        var failed = reported is not null && !newer;
        var answered = failed || (stored.SyncRequestedAt is { } at && syncedAt is { } synced && synced >= at);
        var requested = answered ? null : stored.SyncRequestedAt;
        var error = reported ?? (newer ? null : stored.SyncError);
        return Capped(new CommentsState(comments, MergeIdeas(stored.Ideas, incoming.Ideas), syncedAt, requested, error), live);
    }

    /// <summary>
    /// A comment waiting on a YouTube action that no longer has a live one on its target, <paramref name="live"/>: an
    /// approved reply goes back to drafted without its approved text, an approved hold back to quarantined, and a reply
    /// left replying becomes failed, because YouTube may already have it. Null when no comment changed.
    /// </summary>
    public static CommentsState? Reconcile(CommentsState state, IReadOnlySet<string> live)
    {
        var changed = false;
        var comments = state.Comments.Select(c =>
        {
            if (live.Contains(TargetOf(c.Id)) || c.Status is not (ReplyApproved or HoldApproved or Replying))
            {
                return c;
            }

            changed = true;
            return c.Status switch
            {
                ReplyApproved => c with { Status = Drafted, ApprovedText = null },
                HoldApproved => c with { Status = Quarantined },
                _ => c with { Status = Failed, Error = LeftReplying },
            };
        }).ToList();
        return changed ? state with { Comments = comments } : null;
    }

    /// <summary>
    /// What stays once the owner disconnects YouTube: no comment, no sync time, request or error, and of the ideas only
    /// the used ones, without their text.
    /// </summary>
    public static CommentsState Purged(CommentsState state) =>
        state with
        {
            Comments = [],
            Ideas = [.. state.Ideas.Where(i => i.Status == Used).Select(i => i with { Text = string.Empty })],
            SyncedAt = null,
            SyncRequestedAt = null,
            SyncError = null,
        };

    /// <summary>
    /// The comments fetched before <paramref name="cutoff"/> removed unless their target is in <paramref name="live"/>,
    /// and the ideas whose comment is gone handled as the cap handles them. Null when nothing changed.
    /// </summary>
    public static CommentsState? Cleared(CommentsState state, DateTimeOffset cutoff, IReadOnlySet<string> live)
    {
        var kept = state with { Comments = [.. state.Comments.Where(c => c.FetchedAt >= cutoff || live.Contains(TargetOf(c.Id)))] };
        var cleared = WithoutOrphanIdeas(kept);
        return cleared.Comments.Count == state.Comments.Count && cleared.Ideas.SequenceEqual(state.Ideas) ? null : cleared;
    }

    private static AudienceComment Merge(AudienceComment before, AudienceComment sent)
    {
        if (!string.Equals(sent.ApprovedText, before.ApprovedText, StringComparison.Ordinal))
        {
            throw new OperatorTamperingException($"Only the owner sets the approved reply of comment '{before.Id}'.");
        }
        if (sent.DecidedAt != before.DecidedAt)
        {
            throw new OperatorTamperingException($"Comment '{before.Id}' carries the owner's decision; the worker may not change it.");
        }

        var normalized = sent with { FetchedAt = before.FetchedAt };
        AudienceComment expected;
        if (IsExecution(before.Status, sent.Status))
        {
            if (sent.Status == Replied && string.IsNullOrWhiteSpace(sent.ReplyId))
            {
                throw new OperatorValidationException($"Comment '{before.Id}' is replied only with the reply's id.");
            }

            expected = before with
            {
                Status = sent.Status,
                ReplyId = sent.Status == Replied ? sent.ReplyId : before.ReplyId,
                Error = sent.Status == Failed ? sent.Error : before.Error,
            };
        }
        else if (WorkerStatuses.Contains(before.Status, StringComparer.Ordinal)
            && (sent.Status == before.Status
                || (before.Status is New or DraftRequested && sent.Status == Drafted)
                || (before.Status == New && sent.Status == Quarantined)))
        {
            if (before.DecidedAt is not null && (sent.Kind != before.Kind || (sent.Status == Quarantined && before.Status != Quarantined)))
            {
                throw new OperatorTamperingException(
                    $"Comment '{before.Id}' carries the owner's decision; the worker may not quarantine it or change its kind.");
            }

            expected = before with { Kind = sent.Kind, Theme = sent.Theme, Draft = sent.Draft, Status = sent.Status };
        }
        else if (sent.Status == before.Status)
        {
            expected = before;
        }
        else
        {
            throw new OperatorTamperingException($"Only the owner moves comment '{before.Id}' from {before.Status} to {sent.Status}.");
        }

        return expected == normalized
            ? expected
            : throw new OperatorTamperingException(
                $"The worker may not change comment '{before.Id}' beyond its kind, theme and draft while it is {before.Status}.");
    }

    private static AudienceComment Added(AudienceComment comment, DateTimeOffset now)
    {
        if (!AddedStatuses.Contains(comment.Status, StringComparer.Ordinal))
        {
            throw new OperatorTamperingException(
                $"A new comment is added as {string.Join(", ", AddedStatuses)}; comment '{comment.Id}' was {comment.Status}.");
        }
        if (comment.ApprovedText is not null || comment.DecidedAt is not null || comment.ReplyId is not null)
        {
            throw new OperatorTamperingException($"A new comment carries no approval, decision or reply; comment '{comment.Id}' did.");
        }

        return comment with { FetchedAt = now };
    }

    private static List<AudienceIdea> MergeIdeas(IReadOnlyList<AudienceIdea> stored, IReadOnlyList<AudienceIdea> incoming)
    {
        var sent = incoming.ToHashSet();
        var known = stored.ToHashSet();
        foreach (var decided in stored.Where(i => i.Status != Proposed))
        {
            if (!sent.Contains(decided))
            {
                throw new OperatorTamperingException(
                    $"Idea '{decided.Id}' carries the owner's decision; the worker may not change or drop it.");
            }
        }

        if (incoming.FirstOrDefault(i => (i.Status != Proposed || i.ProjectId is not null) && !known.Contains(i)) is { } undecided)
        {
            throw new OperatorTamperingException($"Only the owner marks an idea {undecided.Status}; idea '{undecided.Id}' was not.");
        }

        return [.. incoming];
    }

    private static CommentsState Capped(CommentsState state, IReadOnlySet<string> live)
    {
        var capped = state;
        if (state.Comments.Count > MaxComments)
        {
            bool Open(AudienceComment c) => live.Contains(TargetOf(c.Id));
            var newest = state.Comments
                .Where(c => !Open(c))
                .OrderByDescending(c => c.PublishedAt)
                .Take(Math.Max(0, MaxComments - state.Comments.Count(Open)))
                .Select(c => c.Id)
                .ToHashSet(StringComparer.Ordinal);
            capped = WithoutOrphanIdeas(state with { Comments = [.. state.Comments.Where(c => Open(c) || newest.Contains(c.Id))] });
        }

        if (capped.Ideas.Count <= MaxIdeas)
        {
            return capped;
        }

        var published = capped.Comments.ToDictionary(c => c.Id, c => c.PublishedAt, StringComparer.Ordinal);
        var kept = capped.Ideas
            .Where(i => i.Status != Used)
            .OrderByDescending(i => published.GetValueOrDefault(i.CommentId, DateTimeOffset.MinValue))
            .Take(Math.Max(0, MaxIdeas - capped.Ideas.Count(i => i.Status == Used)))
            .Select(i => i.Id)
            .ToHashSet(StringComparer.Ordinal);
        return capped with { Ideas = [.. capped.Ideas.Where(i => i.Status == Used || kept.Contains(i.Id))] };
    }

    private static CommentsState WithoutOrphanIdeas(CommentsState state)
    {
        var ids = state.Comments.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        return state with
        {
            Ideas = [.. state.Ideas
                .Where(i => i.Status == Used || ids.Contains(i.CommentId))
                .Select(i => i.Status == Used && !ids.Contains(i.CommentId) ? i with { Text = string.Empty } : i)],
        };
    }

    private static void Validate(CommentsState state)
    {
        foreach (var comment in state.Comments)
        {
            if (!CommentId().IsMatch(comment.Id))
            {
                throw new OperatorValidationException($"'{comment.Id}' is not a comment id.");
            }
            if (!Kinds.Contains(comment.Kind, StringComparer.Ordinal) || !Statuses.Contains(comment.Status, StringComparer.Ordinal))
            {
                throw new OperatorValidationException(
                    $"Comment '{comment.Id}' is a {string.Join(", ", Kinds)} comment, {string.Join(", ", Statuses)}.");
            }
            if (comment.Text.Length > MaxText || comment.Draft.Length > MaxDraft || comment.Theme.Length > MaxTheme)
            {
                throw new OperatorValidationException(
                    $"Comment '{comment.Id}' keeps at most {MaxText} characters of text, a draft of {MaxDraft} and a theme of {MaxTheme}.");
            }
        }

        if (state.Comments.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != state.Comments.Count)
        {
            throw new OperatorValidationException("Every comment needs an id of its own.");
        }
        if (state.Ideas.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != state.Ideas.Count
            || state.Ideas.Any(i => string.IsNullOrWhiteSpace(i.Id)))
        {
            throw new OperatorValidationException("Every idea needs an id of its own.");
        }

        foreach (var idea in state.Ideas)
        {
            if (!IdeaStatuses.Contains(idea.Status, StringComparer.Ordinal) || idea.Text.Length > MaxIdeaText)
            {
                throw new OperatorValidationException(
                    $"Idea '{idea.Id}' is {string.Join(", ", IdeaStatuses)}, with at most {MaxIdeaText} characters of text.");
            }
        }
    }

    [GeneratedRegex(@"\A[A-Za-z0-9_.-]{1,100}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CommentId();
}
