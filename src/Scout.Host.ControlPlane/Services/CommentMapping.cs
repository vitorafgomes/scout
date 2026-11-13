using Scout.ControlPlane.Contracts;
using Scout.ControlPlane.Operator;
using DomainComment = Scout.ControlPlane.Operator.AudienceComment;
using DomainIdea = Scout.ControlPlane.Operator.AudienceIdea;
using WireComment = Scout.ControlPlane.Contracts.AudienceComment;
using WireIdea = Scout.ControlPlane.Contracts.AudienceIdea;

namespace Scout.Host.ControlPlane.Services;

/// <summary>
/// Maps the stored comments, ideas and sync state to and from the <c>CommentsView</c> message. A comment's fetch time
/// the sender leaves empty is read as unset; the control plane stamps it.
/// </summary>
internal static class CommentMapping
{
    public static CommentsView ToWire(CommentsState state)
    {
        var wire = new CommentsView();
        wire.Comments.AddRange(state.Comments.Select(ToWire));
        wire.Ideas.AddRange(state.Ideas.Select(i =>
        {
            var idea = new WireIdea { Id = i.Id, Text = i.Text, CommentId = i.CommentId, Status = i.Status };
            if (i.ProjectId is not null)
            {
                idea.ProjectId = i.ProjectId;
            }
            return idea;
        }));
        if (state.SyncedAt is { } syncedAt)
        {
            wire.SyncedAt = OperatorMapping.Iso(syncedAt);
        }
        if (state.SyncRequestedAt is { } requestedAt)
        {
            wire.SyncRequestedAt = OperatorMapping.Iso(requestedAt);
        }
        if (state.SyncError is not null)
        {
            wire.SyncError = state.SyncError;
        }
        return wire;
    }

    public static CommentsState ToDomain(CommentsView wire) => new(
        wire.Comments.Select(c => new DomainComment(
            c.Id,
            c.ThreadId,
            c.VideoId,
            c.VideoTitle,
            c.Author,
            c.Text,
            OperatorMapping.ParseIso(c.PublishedAt),
            c.LikeCount,
            c.Kind,
            c.Theme,
            c.Status,
            c.Draft,
            c.HasApprovedText ? c.ApprovedText : null,
            c.HasReplyId ? c.ReplyId : null,
            c.HasError ? c.Error : null,
            OperatorMapping.ToDomainTime(c.HasDecidedAt, c.DecidedAt),
            c.FetchedAt.Length == 0 ? default : OperatorMapping.ParseIso(c.FetchedAt))).ToList(),
        wire.Ideas.Select(i => new DomainIdea(i.Id, i.Text, i.CommentId, i.Status, i.HasProjectId ? i.ProjectId : null)).ToList(),
        OperatorMapping.ToDomainTime(wire.HasSyncedAt, wire.SyncedAt),
        OperatorMapping.ToDomainTime(wire.HasSyncRequestedAt, wire.SyncRequestedAt),
        wire.HasSyncError ? wire.SyncError : null);

    private static WireComment ToWire(DomainComment comment)
    {
        var wire = new WireComment
        {
            Id = comment.Id,
            ThreadId = comment.ThreadId,
            VideoId = comment.VideoId,
            VideoTitle = comment.VideoTitle,
            Author = comment.Author,
            Text = comment.Text,
            PublishedAt = OperatorMapping.Iso(comment.PublishedAt),
            LikeCount = comment.LikeCount,
            Kind = comment.Kind,
            Theme = comment.Theme,
            Status = comment.Status,
            Draft = comment.Draft,
            FetchedAt = OperatorMapping.Iso(comment.FetchedAt),
        };
        if (comment.ApprovedText is not null)
        {
            wire.ApprovedText = comment.ApprovedText;
        }
        if (comment.ReplyId is not null)
        {
            wire.ReplyId = comment.ReplyId;
        }
        if (comment.Error is not null)
        {
            wire.Error = comment.Error;
        }
        if (comment.DecidedAt is { } decidedAt)
        {
            wire.DecidedAt = OperatorMapping.Iso(decidedAt);
        }
        return wire;
    }
}
