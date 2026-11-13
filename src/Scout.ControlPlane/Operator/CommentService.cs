using Scout.ControlPlane.Actions;

namespace Scout.ControlPlane.Operator;

/// <summary>
/// The comments on the owner's channel. The desktop lists them, asks for a sync or a draft, dismisses a comment,
/// marks a quarantined one not spam, approves a hold on YouTube or a reply, each creating the one approved YouTube
/// action it needs, and uses or dismisses an idea; the worker reads and saves them under the owner's write grant of
/// the YouTube feature, each save recorded in the write log, and after a revoke saves only the steps of a reply or
/// hold it is executing. The worker never approves, decides or drops anything.
/// </summary>
/// <remarks>Build the service over the operator's store and the operator service that holds the grant, the approvals and the queue.</remarks>
public sealed class CommentService(JsonFileOperatorStore store, OperatorService @operator)
{
    private const string Target = "comments";
    private const string Superseded = "superseded by a new reply";
    private const string DismissedReason = "dismissed by the owner";
    private const string OnYouTube = "This comment already has a reply on YouTube.";
    private const string Running = "A YouTube action on this comment is running; wait for it to end.";
    private const int MaxReplyLength = 1000;
    private const int ReplyQuote = 200;
    private const int HoldQuote = 120;

    private readonly JsonFileOperatorStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly OperatorService _operator = @operator ?? throw new ArgumentNullException(nameof(@operator));

    /// <summary>The stored comments, newest first, with the ideas and the sync's state; it needs no grant.</summary>
    public async Task<CommentsState> ListCommentsAsync(CancellationToken ct) =>
        Newest(await _store.ReadCommentsAsync(ct).ConfigureAwait(false));

    /// <summary>Ask the worker for a sync under the owner's write grant: the request is stamped now.</summary>
    /// <exception cref="OperatorWriteRefusedException">The owner has not granted the YouTube writes.</exception>
    public async Task<CommentsState> RequestCommentSyncAsync(CancellationToken ct)
    {
        await _operator.RequireGrantAsync(Target, ct).ConfigureAwait(false);
        return Newest(await _store.UpdateCommentsAsync(state => state with { SyncRequestedAt = DateTimeOffset.UtcNow }, ct)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// Record the owner's decision on one comment: <c>draft</c> asks the worker for a draft of a new, drafted, failed or
    /// dismissed comment that is not spam, under the grant; <c>dismiss</c> hides a comment no YouTube action is running
    /// on and that is not replying under a live action, rejecting its approved reply or hold; a comment with a reply on
    /// YouTube gets no draft; <c>not_spam</c> returns a quarantined comment to new as other; <c>hold</c> approves, under the grant, holding
    /// a quarantined comment for review on YouTube, one approved action logged as the owner's.
    /// </summary>
    /// <exception cref="OperatorValidationException">The decision, or the comment's status, does not allow it.</exception>
    /// <exception cref="OperatorWriteRefusedException">The owner has not granted the YouTube writes.</exception>
    /// <exception cref="OperatorNotFoundException">No comment has <paramref name="commentId"/>.</exception>
    public async Task<CommentsState> DecideCommentAsync(string commentId, string decision, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        switch (decision)
        {
            case "draft":
                await _operator.RequireGrantAsync(CommentRules.TargetOf(commentId), ct).ConfigureAwait(false);
                return await ChangeAsync(
                        commentId,
                        c => c.ReplyId is not null
                            ? throw new OperatorValidationException(OnYouTube)
                            : c.Status is CommentRules.New or CommentRules.Drafted or CommentRules.Failed or CommentRules.Dismissed
                            && c.Kind != CommentRules.Spam
                            ? c with { Status = CommentRules.DraftRequested, DecidedAt = now, ApprovedText = null, Error = null }
                            : throw new OperatorValidationException(
                                $"Only a new, drafted, failed or dismissed comment that is not spam gets a draft; this one is {c.Status}, {c.Kind}."),
                        ct)
                    .ConfigureAwait(false);
            case "dismiss":
                var target = CommentRules.TargetOf(commentId);
                var live = await _operator.LiveCommentActionsAsync(ct).ConfigureAwait(false);
                var liveTargets = OperatorService.TargetsOf(live);
                RefuseDismiss(Find(await _store.ReadCommentsAsync(ct).ConfigureAwait(false), commentId), liveTargets);
                if (Executing(live).Contains(target))
                {
                    throw new OperatorValidationException(Running);
                }

                await _operator.CancelAsync((action, _) => action.Target == target, DismissedReason, ct).ConfigureAwait(false);
                return await ChangeAsync(
                        commentId,
                        c =>
                        {
                            RefuseDismiss(c, liveTargets);
                            return c with { Status = CommentRules.Dismissed, DecidedAt = now };
                        },
                        ct)
                    .ConfigureAwait(false);
            case "not_spam":
                return await ChangeAsync(
                        commentId,
                        c => c.Status == CommentRules.Quarantined
                            ? c with { Status = CommentRules.New, Kind = CommentRules.Other, DecidedAt = now }
                            : throw new OperatorValidationException($"Only a quarantined comment is marked not spam; this one is {c.Status}."),
                        ct)
                    .ConfigureAwait(false);
            case "hold":
                await _operator.RequireGrantAsync(CommentRules.TargetOf(commentId), ct).ConfigureAwait(false);
                AudienceComment? quarantined = null;
                var held = await ChangeAsync(
                        commentId,
                        c =>
                        {
                            quarantined = c;
                            return c.Status == CommentRules.Quarantined
                                ? c with { Status = CommentRules.HoldApproved, DecidedAt = now }
                                : throw new OperatorValidationException($"Only a quarantined comment is held for review on YouTube; this one is {c.Status}.");
                        },
                        ct)
                    .ConfigureAwait(false);
                var comment = held.Comments.First(c => c.Id == commentId);
                await ApproveOrRestoreAsync(
                        quarantined!,
                        comment,
                        [(CommentRules.Hold, CommentRules.TargetOf(commentId), $"Hold for review on YouTube: {comment.Author} on \"{comment.VideoTitle}\": \"{First(comment.Text, HoldQuote)}\"")],
                        ct)
                    .ConfigureAwait(false);
                return held;
            default:
                throw new OperatorValidationException("A comment is decided as draft, dismiss, not_spam or hold.");
        }
    }

    /// <summary>
    /// Approve, under the owner's write grant, the owner's reply to a comment that is not quarantined or held, has no
    /// reply on YouTube and has no YouTube action running: the text is trimmed, 1 to 1000 characters with no control character
    /// but a new line. The text is kept as the approved one, one approved reply action is queued and logged as the owner's,
    /// and only then is an earlier approved reply on the comment rejected as superseded. When the new action cannot be
    /// queued, or its approval cannot be logged, the comment is returned to what it was and the earlier reply stays approved.
    /// </summary>
    /// <exception cref="OperatorValidationException">The text, or the comment's status, does not allow it.</exception>
    /// <exception cref="OperatorWriteRefusedException">The owner has not granted the YouTube writes.</exception>
    /// <exception cref="OperatorNotFoundException">No comment has <paramref name="commentId"/>.</exception>
    public async Task<CommentsState> ApproveReplyAsync(string commentId, string text, CancellationToken ct)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length is 0 or > MaxReplyLength)
        {
            throw new OperatorValidationException($"A reply is 1 to {MaxReplyLength} characters.");
        }
        if (trimmed.Any(c => char.IsControl(c) && c != '\n'))
        {
            throw new OperatorValidationException("A reply carries no control character other than a new line.");
        }

        var target = CommentRules.TargetOf(commentId);
        await _operator.RequireGrantAsync(target, ct).ConfigureAwait(false);
        var live = await _operator.LiveCommentActionsAsync(ct).ConfigureAwait(false);
        var liveTargets = OperatorService.TargetsOf(live);
        RefuseReply(Find(await _store.ReadCommentsAsync(ct).ConfigureAwait(false), commentId), liveTargets);
        if (Executing(live).Contains(target))
        {
            throw new OperatorValidationException(Running);
        }

        AudienceComment? earlier = null;
        var approved = await ChangeAsync(
                commentId,
                c =>
                {
                    RefuseReply(c, liveTargets);
                    earlier = c;
                    return c with { Status = CommentRules.ReplyApproved, ApprovedText = trimmed, DecidedAt = DateTimeOffset.UtcNow, Error = null };
                },
                ct)
            .ConfigureAwait(false);
        var comment = approved.Comments.First(c => c.Id == commentId);
        var queued = await ApproveOrRestoreAsync(
                earlier!,
                comment,
                [(CommentRules.Reply, target, $"Reply to {comment.Author} on \"{comment.VideoTitle}\": \"{First(trimmed, ReplyQuote)}\"")],
                ct)
            .ConfigureAwait(false);
        await _operator.CancelAsync((action, _) => action.Target == target && !queued.Any(q => q.Id == action.Id), Superseded, ct)
            .ConfigureAwait(false);
        return approved;
    }

    /// <summary>
    /// Record the owner's decision on a proposed idea: used checks the grant, claims the idea, then starts a project on its
    /// text the way the desktop starts one, under the same grant, and keeps the project's id, returning the idea to proposed
    /// when the project cannot be started; not used dismisses it.
    /// </summary>
    /// <exception cref="OperatorValidationException">The idea is already decided, or its text is not a topic.</exception>
    /// <exception cref="OperatorWriteRefusedException">Used without the owner's grant of the YouTube writes.</exception>
    /// <exception cref="OperatorNotFoundException">No idea has <paramref name="ideaId"/>.</exception>
    public async Task<CommentsState> DecideIdeaAsync(string ideaId, bool use, CancellationToken ct)
    {
        if (use)
        {
            var idea = Proposed(await _store.ReadCommentsAsync(ct).ConfigureAwait(false), ideaId);
            await _operator.RequireGrantAsync(idea.Text, ct).ConfigureAwait(false);
        }

        AudienceIdea? proposed = null;
        var decided = await _store.UpdateCommentsAsync(
                state =>
                {
                    proposed = Proposed(state, ideaId);
                    return Replace(state, proposed, proposed with { Status = use ? CommentRules.Used : CommentRules.Dismissed });
                },
                ct)
            .ConfigureAwait(false);
        if (!use)
        {
            return Newest(decided);
        }

        var claimed = proposed! with { Status = CommentRules.Used };
        string projectId;
        try
        {
            projectId = (await _operator.StartProjectAsync(proposed.Text, string.Empty, string.Empty, ct).ConfigureAwait(false)).Id;
        }
        catch
        {
            try
            {
                await _store.UpdateCommentsAsync(state => state.Ideas.Contains(claimed) ? Replace(state, claimed, proposed) : null, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
            }

            throw;
        }

        return Newest(await _store.UpdateCommentsAsync(
                state => state.Ideas.Contains(claimed) ? Replace(state, claimed, claimed with { ProjectId = projectId }) : null,
                CancellationToken.None)
            .ConfigureAwait(false));
    }

    /// <summary>The stored comments for the worker under the owner's write grant.</summary>
    /// <exception cref="OperatorWriteRefusedException">The owner has not granted the YouTube writes.</exception>
    public async Task<CommentsState> GetCommentsForWorkerAsync(CancellationToken ct)
    {
        await _operator.RequireGrantAsync(Target, ct).ConfigureAwait(false);
        return await ListCommentsAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Save the worker's comments under the owner's write grant, merged by what the worker may change, and record the
    /// save in the write log. Without the grant only the steps of a reply or hold the worker is executing are saved.
    /// </summary>
    /// <exception cref="OperatorWriteRefusedException">The owner has not granted the YouTube writes.</exception>
    /// <exception cref="OperatorValidationException">A kind, status, id or length the operator does not accept.</exception>
    /// <exception cref="OperatorTamperingException">A change only the owner or YouTube makes.</exception>
    /// <exception cref="OperatorStoreException">The comments file could not be written.</exception>
    public async Task<CommentsState> SaveCommentsFromWorkerAsync(CommentsState comments, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(comments);

        var refusal = await _operator.GrantRefusalAsync(Target, ct).ConfigureAwait(false);
        var live = await _operator.LiveCommentActionsAsync(ct).ConfigureAwait(false);
        var liveTargets = OperatorService.TargetsOf(live);
        var executing = Executing(live);
        if (refusal is not null && executing.Count == 0)
        {
            throw new OperatorWriteRefusedException(refusal);
        }

        var saved = await _store.UpdateCommentsAsync(
                stored =>
                {
                    var merged = CommentRules.MergeFromWorker(stored, comments, DateTimeOffset.UtcNow, liveTargets);
                    return refusal is null || OnlyExecution(stored, merged, executing)
                        ? merged
                        : throw new OperatorWriteRefusedException(refusal);
                },
                ct)
            .ConfigureAwait(false);
        await _operator.LogAsync(Target, "saved comments", approvedBy: null, ct).ConfigureAwait(false);
        return Newest(saved);
    }

    private static bool OnlyExecution(CommentsState stored, CommentsState merged, HashSet<string> executing)
    {
        var before = stored.Comments.ToDictionary(c => c.Id, StringComparer.Ordinal);
        return merged.Comments.Count == stored.Comments.Count
            && merged.Comments.All(m => before.TryGetValue(m.Id, out var b)
                && (m == b || (executing.Contains(CommentRules.TargetOf(m.Id)) && CommentRules.IsExecution(b.Status, m.Status))))
            && merged.Ideas.SequenceEqual(stored.Ideas)
            && merged.SyncedAt == stored.SyncedAt
            && merged.SyncRequestedAt == stored.SyncRequestedAt
            && string.Equals(merged.SyncError, stored.SyncError, StringComparison.Ordinal);
    }

    private static void RefuseDismiss(AudienceComment comment, HashSet<string> live)
    {
        if (comment.Status == CommentRules.Replying && live.Contains(CommentRules.TargetOf(comment.Id)))
        {
            throw new OperatorValidationException(Running);
        }
    }

    private static void RefuseReply(AudienceComment comment, HashSet<string> live)
    {
        if (comment.Status is CommentRules.Quarantined or CommentRules.HoldApproved or CommentRules.Held)
        {
            throw new OperatorValidationException("A quarantined comment gets no reply; mark it not spam first.");
        }
        if (comment.ReplyId is not null || comment.Status == CommentRules.Replied)
        {
            throw new OperatorValidationException(OnYouTube);
        }
        if (comment.Status == CommentRules.Replying && live.Contains(CommentRules.TargetOf(comment.Id)))
        {
            throw new OperatorValidationException(Running);
        }
    }

    private static HashSet<string> Executing(IEnumerable<PendingAction> live) =>
        OperatorService.TargetsOf(live.Where(a => a.Status == PendingActionStatus.Executing));

    private async Task<CommentsState> ChangeAsync(string commentId, Func<AudienceComment, AudienceComment> change, CancellationToken ct) =>
        Newest(await _store.UpdateCommentsAsync(
                state =>
                {
                    var stored = Find(state, commentId);
                    var changed = change(stored);
                    return state with { Comments = [.. state.Comments.Select(c => c.Id == commentId ? changed : c)] };
                },
                ct)
            .ConfigureAwait(false));

    private async Task<IReadOnlyList<PendingAction>> ApproveOrRestoreAsync(
        AudienceComment prior,
        AudienceComment approved,
        IReadOnlyList<(string Action, string Target, string Summary)> actions,
        CancellationToken ct)
    {
        try
        {
            return await _operator.ApproveAsync(actions, ct).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await _store.UpdateCommentsAsync(
                        state => state.Comments.Contains(approved)
                            ? state with { Comments = [.. state.Comments.Select(c => c == approved ? prior : c)] }
                            : null,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
            }

            throw;
        }
    }

    private static CommentsState Replace(CommentsState state, AudienceIdea from, AudienceIdea to) =>
        state with { Ideas = [.. state.Ideas.Select(i => i == from ? to : i)] };

    private static AudienceComment Find(CommentsState state, string commentId) =>
        state.Comments.FirstOrDefault(c => string.Equals(c.Id, commentId, StringComparison.Ordinal))
        ?? throw new OperatorNotFoundException($"Unknown comment '{commentId}'.");

    private static AudienceIdea Proposed(CommentsState state, string ideaId)
    {
        var idea = state.Ideas.FirstOrDefault(i => string.Equals(i.Id, ideaId, StringComparison.Ordinal))
            ?? throw new OperatorNotFoundException($"Unknown idea '{ideaId}'.");
        return idea.Status == CommentRules.Proposed
            ? idea
            : throw new OperatorValidationException($"Idea '{ideaId}' is already {idea.Status}.");
    }

    private static string First(string text, int length) => text.Length <= length ? text : text[..length];

    private static CommentsState Newest(CommentsState state) =>
        state with { Comments = [.. state.Comments.OrderByDescending(c => c.PublishedAt)] };
}
