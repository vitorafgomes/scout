using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scout.Conversation.Abstractions;
using Scout.Plugin.YouTube;
using Scout.Plugin.YouTube.Abstractions;

namespace Scout.Operator;

/// <summary>
/// Runs, every <see cref="Interval"/>, the YouTube action the control plane hands out: the oldest one the owner approved,
/// claimed for this run. It is an upload of a project's package with the owner's publish settings, its thumbnail, or an
/// experiment arm's title or thumbnail. Only the action handed out is run, and every run is safe to repeat: a project that
/// already has a video id is never uploaded again, the video id is saved as soon as YouTube accepts the upload and before
/// the run is recorded, an upload whose save failed is saved on the next run from the id kept in this process, an
/// interrupted upload resumes the session this process kept for the action, an upload found <c>uploading</c> that this
/// process knows nothing of fails rather than risk a second video, and a title YouTube already shows is not sent again. An
/// arm switch for an experiment that ended changes nothing on YouTube and is recorded skipped; the restore to the control
/// and the adopted winner still run. A run that succeeds is recorded, which consumes the action; a write YouTube took whose
/// save the control plane refuses is still recorded, as succeeded but not saved, so a performed write is never left
/// unrecorded; a record refused because the owner revoked YouTube writes after approving is logged, the control plane having
/// logged it "after revoke", and the revoke is saved on the publication when the control plane still takes that save. A failure YouTube or the control plane may recover from leaves the action for the next run, with the reason
/// saved on the publication; any other failure is saved and recorded as failed, which consumes the action, so the queue
/// moves on. When the project came from a chat, that chat hears the upload, with its link or its publish time on the London
/// clock, and a failure that consumed the action; a post that fails is logged and never fails the action. A comment action,
/// on <c>comment:&lt;id&gt;</c>, posts the owner's approved reply text, never the draft, once: the comment is saved
/// <c>replying</c> before the post and <c>replied</c> with the reply's id after it, a reply id kept in this process is saved
/// without a second post, and a comment found <c>replying</c> that this process cannot prove unposted fails rather than risk
/// a second reply; or it holds the comment for review on YouTube, which is safe to repeat, and saves it <c>held</c>. A comment
/// action that fails for good saves the comment <c>failed</c> with the reason; one that can pass is left for the next run.
/// Just before every irreversible call, and before the save that marks an upload or a reply as started, the owner's grant is
/// read once more: a grant gone, which the control plane answers by rejecting the action as writes were revoked, or a read
/// that fails, sends nothing, saves nothing and records nothing.
/// </summary>
internal sealed class ActionExecutor(
    IOperatorControlPlane controlPlane,
    IYouTubeWriteClient youtube,
    IOptions<OperatorMediaOptions> options,
    TimeProvider time,
    ILogger<ActionExecutor> logger,
    IOriginNotifier notifier,
    CommentDesk comments) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    internal const string FeatureId = "youtube";
    internal const string Insert = "videos.insert";
    internal const string Update = "videos.update";
    internal const string Thumbnail = "thumbnails.set";
    internal const string Reply = "comments.insert";
    internal const string Hold = "comments.setModerationStatus";

    internal const string PublishApproved = "publish_approved";
    internal const string Uploading = "uploading";
    internal const string UploadedStatus = "uploaded";
    internal const string Scheduled = "scheduled";
    internal const string Published = "published";
    internal const string Failed = "failed";
    internal const string Running = "running";

    internal const string RevokedAfterApproval =
        "The owner revoked YouTube writes after approving this action; the change on YouTube stays and is shown here.";

    internal const string MayAlreadyBeUploaded =
        "The upload may already be on YouTube; check YouTube Studio before publishing again.";

    internal const string MayAlreadyBeReplied =
        "The reply may already be on YouTube; check the video's comments in YouTube Studio before replying again.";

    internal const string SkippedEnded = "skipped: the experiment ended";

    internal const string Concluded = "concluded";
    internal const string Adopted = "adopted";

    private readonly ConcurrentDictionary<string, string> _uploadedNotSaved = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, UploadSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _repliedNotSaved = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _notPosted = new(StringComparer.Ordinal);

    /// <summary>How many upload sessions this process keeps for actions not ended yet.</summary>
    internal int OpenSessions => _sessions.Count;
    private bool _pollFailureLogged;

    internal static string NoPublishSettings(string topic) =>
        $"{topic} has no publish settings from the owner; publish it from Scout Desktop, YouTube operator.";

    internal static string NotApproved(string topic) => $"The video of {topic} is not approved; approve the package in Scout Desktop first.";

    internal static string MissingFile(string file) => $"{file} is no longer in the project's media folder; assemble the video again.";

    internal static string NoVideo(string topic) => $"{topic} has no video on YouTube to change.";

    internal static string WaitsForUpload(string topic) => $"{topic} is not uploaded yet; its thumbnail waits for the upload.";

    internal static string Unsupported(string action, string target) => $"Scout does not run {action} on {target}.";

    internal static string NoArm(string target) => $"The experiment arm {target} does not exist.";

    internal static string UnknownComment(string id) => $"Comment {id} is no longer in Scout.";

    internal static string NotApprovedReply(string id, string status) => $"Comment {id} is {status}; only a reply the owner approved is posted.";

    internal static string NotApprovedHold(string id, string status) => $"Comment {id} is {status}; only a hold the owner approved is run.";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            do
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Run the oldest approved action, if one waits.</summary>
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        OperatorAction? action;
        try
        {
            action = await controlPlane.NextApprovedActionAsync(ct).ConfigureAwait(false);
            _pollFailureLogged = false;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (!_pollFailureLogged)
            {
                logger.LogWarning("The operator cannot read approved YouTube actions: {Reason}", OperatorPipeline.Reason(ex));
                _pollFailureLogged = true;
            }

            return;
        }

        if (action is null)
        {
            return;
        }

        var outcome = "succeeded";
        try
        {
            outcome = await RunAsync(action, ct).ConfigureAwait(false) ?? outcome;
        }
        catch (GrantNotHeldException ex)
        {
            logger.LogWarning("YouTube action {ActionId} ({Action}) was not sent: {Reason}", action.Id, action.Action, ex.Message);
            return;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var reason = Reason(ex);
            var retry = Retryable(ex);
            logger.LogWarning(
                "YouTube action {ActionId} ({Action}) failed ({ExceptionType}); {Next}.",
                action.Id, action.Action, ex.GetType().Name, retry ? "it runs again later" : "it is recorded as failed");
            await SaveErrorAsync(action, reason, retry, ct).ConfigureAwait(false);
            if (retry)
            {
                return;
            }

            _sessions.TryRemove(action.Id, out _);
            _uploadedNotSaved.TryRemove(action.Id, out _);
            _repliedNotSaved.TryRemove(action.Id, out _);
            _notPosted.TryRemove(action.Id, out _);
            outcome = $"failed: {reason}";
        }

        try
        {
            await controlPlane.RecordActionAsync(action, outcome, ct).ConfigureAwait(false);
        }
        catch (WriteRevokedException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("The owner revoked YouTube writes after YouTube action {ActionId} ran; the change on YouTube stays.", action.Id);
            if (outcome.StartsWith("succeeded", StringComparison.Ordinal))
            {
                await SaveErrorAsync(action, RevokedAfterApproval, retry: true, ct).ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException ex) when (!ct.IsCancellationRequested && ex is not ControlPlaneUnavailableException)
        {
            logger.LogWarning("YouTube action {ActionId} ran but the control plane refused to record it: {Reason}", action.Id, ex.Message);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("YouTube action {ActionId} ran but could not be recorded ({ExceptionType}).", action.Id, ex.GetType().Name);
        }
    }

    private async Task<string?> RunAsync(OperatorAction action, CancellationToken ct)
    {
        if (action.FeatureId != FeatureId)
        {
            throw new InvalidOperationException(Unsupported(action.Action, action.Target));
        }

        if (action.Target.StartsWith(CommentDesk.TargetPrefix, StringComparison.Ordinal))
        {
            var commentId = action.Target[CommentDesk.TargetPrefix.Length..];
            return action.Action switch
            {
                Reply => await ReplyAsync(action, commentId, ct).ConfigureAwait(false),
                Hold => await HoldAsync(action, commentId, ct).ConfigureAwait(false),
                _ => throw new InvalidOperationException(Unsupported(action.Action, action.Target)),
            };
        }

        var parts = action.Target.Split(':');
        switch (action.Action, parts.Length)
        {
            case (Insert, 1):
                return await InsertAsync(action, parts[0], ct).ConfigureAwait(false);
            case (Thumbnail, 1):
                await SetThumbnailAsync(action, parts[0], ct).ConfigureAwait(false);
                return null;
            case (Update or Thumbnail, 3):
                return await SetArmAsync(action, parts[0], parts[1], parts[2], ct).ConfigureAwait(false);
            default:
                throw new InvalidOperationException(Unsupported(action.Action, action.Target));
        }
    }

    private async Task<string?> InsertAsync(OperatorAction action, string projectId, CancellationToken ct)
    {
        var actionId = action.Id;
        var project = await controlPlane.GetProjectAsync(projectId, ct).ConfigureAwait(false);
        var publication = project.Publication;
        if (publication?.VideoId is not null)
        {
            return null;
        }

        if (_uploadedNotSaved.TryGetValue(actionId, out var kept))
        {
            return await SaveUploadedAsync(actionId, projectId, kept, ct).ConfigureAwait(false);
        }

        if (publication?.Status == Uploading && !_sessions.ContainsKey(actionId))
        {
            throw new InvalidOperationException(MayAlreadyBeUploaded);
        }

        if (publication is null || publication.Status is not (PublishApproved or Uploading))
        {
            throw new InvalidOperationException(NoPublishSettings(project.Topic));
        }

        if (project.Package is not { VideoFile: { } videoFile, ApprovedAt: not null } package)
        {
            throw new InvalidOperationException(NotApproved(project.Topic));
        }

        var path = await MediaPathAsync(projectId, videoFile, ct).ConfigureAwait(false);
        var mandate = await controlPlane.GetMandateAsync(ct).ConfigureAwait(false);
        var publishAt = publication.PublishAt is null
            ? (DateTimeOffset?)null
            : DateTimeOffset.Parse(publication.PublishAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        var upload = new YouTubeUpload(
            package.Title,
            package.Description,
            package.Tags,
            options.Value.CategoryId,
            LanguageCode(mandate.Language),
            publishAt is null ? publication.PrivacyStatus : "private",
            publishAt,
            publication.MadeForKids,
            publication.SyntheticMedia);
        await RequireGrantAsync(action, ct).ConfigureAwait(false);
        var session = _sessions.GetOrAdd(actionId, _ => new UploadSession());
        await SavePublicationAsync(projectId, p => p with { Status = Uploading, Error = null }, ct).ConfigureAwait(false);
        await RequireGrantAsync(action, ct).ConfigureAwait(false);
        var videoId = await youtube.UploadVideoAsync(path, upload, session, ct).ConfigureAwait(false);
        _uploadedNotSaved[actionId] = videoId;
        _sessions.TryRemove(actionId, out _);
        var outcome = await SaveUploadedAsync(actionId, projectId, videoId, ct).ConfigureAwait(false);
        logger.LogInformation("Video project {ProjectId} was uploaded to YouTube.", projectId);
        await NotifyAsync(
            action,
            publishAt is { } at
                ? YouTubeOperatorPlugin.ScheduledNotice(project.Topic, at, videoId)
                : YouTubeOperatorPlugin.UploadedNotice(project.Topic, publication.PrivacyStatus, videoId),
            ct).ConfigureAwait(false);
        return outcome;
    }

    private async Task<string?> SaveUploadedAsync(string actionId, string projectId, string videoId, CancellationToken ct)
    {
        try
        {
            await SavePublicationAsync(
                projectId,
                p => p with
                {
                    Status = p.PublishAt is not null ? Scheduled : p.PrivacyStatus == "public" ? Published : UploadedStatus,
                    VideoId = videoId,
                    UploadedAt = Iso(time.GetUtcNow()),
                    Error = null,
                },
                ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (!ct.IsCancellationRequested && ex is not ControlPlaneUnavailableException)
        {
            logger.LogWarning("YouTube accepted the upload of project {ProjectId}, but the control plane refused to save it: {Reason}", projectId, ex.Message);
            _uploadedNotSaved.TryRemove(actionId, out _);
            return $"succeeded: video {videoId}, not saved";
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            throw new UploadNotSavedException(ex);
        }

        _uploadedNotSaved.TryRemove(actionId, out _);
        return null;
    }

    private async Task SetThumbnailAsync(OperatorAction action, string projectId, CancellationToken ct)
    {
        var project = await controlPlane.GetProjectAsync(projectId, ct).ConfigureAwait(false);
        if (project.Publication?.VideoId is not { } videoId)
        {
            throw project.Publication?.Status is PublishApproved or Uploading
                ? new ActionWaitsException(WaitsForUpload(project.Topic))
                : new InvalidOperationException(NoVideo(project.Topic));
        }

        var file = project.Package?.ThumbnailFile ?? throw new InvalidOperationException(MissingFile("thumbnail.png"));
        var path = await MediaPathAsync(projectId, file, ct).ConfigureAwait(false);
        await RequireGrantAsync(action, ct).ConfigureAwait(false);
        await youtube.SetThumbnailAsync(videoId, path, ct).ConfigureAwait(false);
    }

    private async Task<string?> SetArmAsync(OperatorAction action, string projectId, string experimentId, string armId, CancellationToken ct)
    {
        var scope = action.Action;
        var project = await controlPlane.GetProjectAsync(projectId, ct).ConfigureAwait(false);
        var target = $"{projectId}:{experimentId}:{armId}";
        if (project.Publication?.Experiment is not { } experiment || experiment.Id != experimentId
            || experiment.Arms.FirstOrDefault(a => a.Id == armId) is not { } arm)
        {
            throw new InvalidOperationException(NoArm(target));
        }

        if (project.Publication.VideoId is not { } videoId)
        {
            throw new InvalidOperationException(NoVideo(project.Topic));
        }

        if (scope != (experiment.Kind == "title" ? Update : Thumbnail))
        {
            throw new InvalidOperationException(Unsupported(scope, target));
        }

        if (experiment.Status != Running && armId != ExperimentRunner.Control
            && !(experiment.Status == Adopted && armId == experiment.WinnerArmId))
        {
            return SkippedEnded;
        }

        if (scope == Update)
        {
            await RequireGrantAsync(action, ct).ConfigureAwait(false);
            await youtube.UpdateTitleAsync(videoId, arm.Value, ct).ConfigureAwait(false);
        }
        else
        {
            var path = await MediaPathAsync(projectId, arm.Value, ct).ConfigureAwait(false);
            await RequireGrantAsync(action, ct).ConfigureAwait(false);
            await youtube.SetThumbnailAsync(videoId, path, ct).ConfigureAwait(false);
        }

        if (experiment.Status != Running)
        {
            return null;
        }

        var now = Iso(time.GetUtcNow());
        try
        {
            await SavePublicationAsync(
            projectId,
            p => p.Experiment?.Id != experimentId
                ? p
                : p with
                {
                    Experiment = p.Experiment with
                    {
                        Arms = [.. p.Experiment.Arms.Select(a => a.Id == armId
                            ? a.StartedAt is null ? a with { StartedAt = now } : a
                            : a.StartedAt is not null && a.EndedAt is null ? a with { EndedAt = now } : a)],
                    },
                },
            ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (!ct.IsCancellationRequested && ex is not ControlPlaneUnavailableException)
        {
            logger.LogWarning("YouTube action on {Target} changed the video, but the control plane refused to save its timing: {Reason}", target, ex.Message);
            return $"succeeded: {scope} {target}, not saved";
        }

        return null;
    }

    private async Task<string?> ReplyAsync(OperatorAction action, string commentId, CancellationToken ct)
    {
        var latest = await controlPlane.GetCommentsAsync(ct).ConfigureAwait(false);
        try
        {
            var comment = latest.Comments.FirstOrDefault(c => c.Id == commentId) ?? throw new InvalidOperationException(UnknownComment(commentId));
            if (comment.ReplyId is not null)
            {
                return null;
            }

            if (_repliedNotSaved.TryGetValue(action.Id, out var kept))
            {
                return await SaveRepliedAsync(action.Id, commentId, kept, latest, ct).ConfigureAwait(false);
            }

            if (comment.Status == CommentDesk.Replying && !_notPosted.ContainsKey(action.Id))
            {
                throw new InvalidOperationException(MayAlreadyBeReplied);
            }

            if (comment.Status is not (CommentDesk.ReplyApproved or CommentDesk.Replying) || string.IsNullOrWhiteSpace(comment.ApprovedText))
            {
                throw new InvalidOperationException(NotApprovedReply(commentId, comment.Status));
            }

            await RequireGrantAsync(action, ct).ConfigureAwait(false);
            if (comment.Status == CommentDesk.ReplyApproved)
            {
                _notPosted[action.Id] = true;
                latest = await comments.ChangeAsync(s => Step(s, commentId, c => c with { Status = CommentDesk.Replying }), latest, ct)
                    .ConfigureAwait(false);
                await RequireGrantAsync(action, ct).ConfigureAwait(false);
            }

            _notPosted.TryRemove(action.Id, out _);
            string replyId;
            try
            {
                replyId = await youtube.ReplyToCommentAsync(commentId, comment.ApprovedText, ct).ConfigureAwait(false);
            }
            catch (YouTubeApiException ex) when (SurelyRefused(ex))
            {
                _notPosted[action.Id] = true;
                throw;
            }
            catch (YouTubeApiException ex) when ((int?)ex.StatusCode is >= 200 and < 300)
            {
                throw new InvalidOperationException(MayAlreadyBeReplied, ex);
            }

            _repliedNotSaved[action.Id] = replyId;
            logger.LogInformation("The owner's reply to comment {CommentId} was posted on YouTube.", commentId);
            return await SaveRepliedAsync(action.Id, commentId, replyId, latest, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && !Retryable(ex) && ex is not GrantNotHeldException)
        {
            await SaveCommentFailedAsync(commentId, Reason(ex), latest, ct).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<string?> SaveRepliedAsync(string actionId, string commentId, string replyId, OperatorComments latest, CancellationToken ct)
    {
        try
        {
            await comments.ChangeAsync(
                    s => Step(s, commentId, c => c with { Status = CommentDesk.Replied, ReplyId = replyId, Error = null }), latest, ct)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (!ct.IsCancellationRequested && ex is not ControlPlaneUnavailableException)
        {
            logger.LogWarning("YouTube took the reply to comment {CommentId}, but the control plane refused to save it: {Reason}", commentId, ex.Message);
            _repliedNotSaved.TryRemove(actionId, out _);
            return $"succeeded: comment {commentId}, not saved";
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            throw new ReplyNotSavedException(ex);
        }

        _repliedNotSaved.TryRemove(actionId, out _);
        return null;
    }

    private async Task<string?> HoldAsync(OperatorAction action, string commentId, CancellationToken ct)
    {
        var latest = await controlPlane.GetCommentsAsync(ct).ConfigureAwait(false);
        try
        {
            var comment = latest.Comments.FirstOrDefault(c => c.Id == commentId) ?? throw new InvalidOperationException(UnknownComment(commentId));
            if (comment.Status == CommentDesk.Held)
            {
                return null;
            }

            if (comment.Status != CommentDesk.HoldApproved)
            {
                throw new InvalidOperationException(NotApprovedHold(commentId, comment.Status));
            }

            await RequireGrantAsync(action, ct).ConfigureAwait(false);
            await youtube.HoldCommentAsync(commentId, ct).ConfigureAwait(false);
            try
            {
                await comments.ChangeAsync(s => Step(s, commentId, c => c with { Status = CommentDesk.Held }), latest, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (!ct.IsCancellationRequested && ex is not ControlPlaneUnavailableException)
            {
                logger.LogWarning("YouTube held comment {CommentId} for review, but the control plane refused to save it: {Reason}", commentId, ex.Message);
                return $"succeeded: comment {commentId}, not saved";
            }

            return null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && !Retryable(ex) && ex is not GrantNotHeldException)
        {
            await SaveCommentFailedAsync(commentId, Reason(ex), latest, ct).ConfigureAwait(false);
            throw;
        }
    }

    private async Task SaveCommentFailedAsync(string commentId, string reason, OperatorComments latest, CancellationToken ct)
    {
        try
        {
            await comments.ChangeAsync(
                    s => Step(s, commentId, c => c.Status is CommentDesk.ReplyApproved or CommentDesk.Replying or CommentDesk.HoldApproved
                        ? c with { Status = CommentDesk.Failed, Error = reason }
                        : c),
                    latest,
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("The failure of the YouTube action on comment {CommentId} could not be saved ({ExceptionType}).", commentId, ex.GetType().Name);
        }
    }

    private static OperatorComments Step(OperatorComments state, string commentId, Func<OperatorComment, OperatorComment> change)
        => state with { Comments = [.. state.Comments.Select(c => c.Id == commentId ? change(c) : c)] };

    private async Task<string> MediaPathAsync(string projectId, string file, CancellationToken ct)
    {
        var directory = await controlPlane.GetMediaDirectoryAsync(projectId, ct).ConfigureAwait(false);
        var path = MediaFiles.PathFor(directory, file);
        return File.Exists(path) ? path : throw new InvalidOperationException(MissingFile(file));
    }

    private async Task SavePublicationAsync(string projectId, Func<OperatorPublication, OperatorPublication> change, CancellationToken ct)
    {
        var current = await controlPlane.GetProjectAsync(projectId, ct).ConfigureAwait(false);
        var publication = current.Publication ?? throw new InvalidOperationException(NoPublishSettings(current.Topic));
        await controlPlane.SaveProjectAsync(current with { Publication = change(publication) }, null, ct).ConfigureAwait(false);
    }

    private async Task SaveErrorAsync(OperatorAction action, string reason, bool retry, CancellationToken ct)
    {
        if (action.FeatureId != FeatureId || action.Target.Contains('.', StringComparison.Ordinal)
            || action.Target.StartsWith(CommentDesk.TargetPrefix, StringComparison.Ordinal))
        {
            return;
        }

        var projectId = action.Target.Split(':')[0];
        try
        {
            var current = await controlPlane.GetProjectAsync(projectId, ct).ConfigureAwait(false);
            if (current.Publication is not { } publication || (publication.VideoId is null && action.Action == Thumbnail && retry))
            {
                return;
            }

            var status = action.Action == Insert && publication.VideoId is null && !retry ? Failed : publication.Status;
            await controlPlane.SaveProjectAsync(current with { Publication = publication with { Status = status, Error = reason } }, null, ct)
                .ConfigureAwait(false);
            if (!retry)
            {
                await NotifyAsync(action, OperatorPipeline.FailedNotice(current.Topic, reason), ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("The failure of YouTube action {ActionId} could not be saved ({ExceptionType}).", action.Id, ex.GetType().Name);
        }
    }

    private async Task NotifyAsync(OperatorAction action, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(action.OriginReference))
        {
            return;
        }

        try
        {
            if (!await notifier.PostAsync(action.OriginReference, text, ct).ConfigureAwait(false))
            {
                logger.LogInformation("The progress of YouTube action {ActionId} could not be posted to the originating conversation.", action.Id);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Posting the progress of YouTube action {ActionId} failed ({ExceptionType}).", action.Id, ex.GetType().Name);
        }
    }

    /// <summary>
    /// A reply YouTube never took: a 4xx refusal, or a failure with no status, which only the token handler raises, before
    /// the request is sent, when the token file is missing. A success status means YouTube took it but its answer was
    /// unreadable; a 5xx, a timeout or a reset may have landed.
    /// </summary>
    private static bool SurelyRefused(YouTubeApiException ex) => ex.StatusCode is null || (int)ex.StatusCode is >= 400 and < 500;

    private static bool Retryable(Exception ex) => ex switch
    {
        ControlPlaneUnavailableException or ActionWaitsException or UploadNotSavedException or ReplyNotSavedException => true,
        YouTubeApiException youtubeError => youtubeError.Retryable,
        HttpRequestException or IOException or TaskCanceledException => true,
        _ => false,
    };

    private static string Reason(Exception ex) => ex switch
    {
        YouTubeApiException or InvalidOperationException => ex.Message,
        UploadNotSavedException or ReplyNotSavedException => ex.Message,
        HttpRequestException or IOException or TaskCanceledException => TopicResearcher.YouTubeUnreachable,
        _ => OperatorPipeline.Reason(ex),
    };

    /// <summary>The language code YouTube takes for the mandate's language (<c>pt-BR</c>, or <c>Portuguese</c> as <c>pt</c>); null when it names none.</summary>
    internal static string? LanguageCode(string language)
    {
        var trimmed = (language ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(trimmed, predefinedOnly: true);
            if (!culture.Equals(CultureInfo.InvariantCulture))
            {
                return culture.Name;
            }
        }
        catch (CultureNotFoundException)
        {
        }

        return CultureInfo.GetCultures(CultureTypes.NeutralCultures)
            .FirstOrDefault(c => !c.Equals(CultureInfo.InvariantCulture)
                && (string.Equals(c.EnglishName, trimmed, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(c.NativeName, trimmed, StringComparison.OrdinalIgnoreCase)))
            ?.Name;
    }

    internal static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private async Task RequireGrantAsync(OperatorAction action, CancellationToken ct)
    {
        bool granted;
        try
        {
            granted = await controlPlane.CheckGrantAsync(action, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            throw new GrantNotHeldException($"Scout could not read YouTube's write permission ({OperatorPipeline.Reason(ex)}), so it sent nothing.");
        }

        if (!granted)
        {
            throw new GrantNotHeldException("YouTube writes were revoked.");
        }
    }

    private sealed class ActionWaitsException(string message) : Exception(message);

    private sealed class GrantNotHeldException(string message) : Exception(message);

    private sealed class ReplyNotSavedException(Exception inner)
        : Exception($"The reply is on YouTube but Scout could not save it: {OperatorPipeline.Reason(inner)}", inner);

    private sealed class UploadNotSavedException(Exception inner)
        : Exception($"The video is on YouTube but Scout could not save it: {OperatorPipeline.Reason(inner)}", inner);
}
