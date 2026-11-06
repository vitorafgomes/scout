using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Features;
using Scout.ControlPlane.Operator;
using Scout.ControlPlane.WriteGate;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Operator;

/// <summary>The comments on the owner's channel over the real catalog, write gate, action queue and Scout's own files in a temp folder.</summary>
public sealed class CommentServiceTests : IDisposable
{
    private const string NotAllowed =
        "The owner has not allowed youtube to write; allow it on the Features screen of Scout Desktop.";

    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly DateTimeOffset Published = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Directory.CreateTempSubdirectory("scout-comments-").FullName;
    private readonly JsonFileFeatureStateStore _states;
    private readonly JsonFileActionQueue _actions;
    private readonly JsonLinesWriteLog _writes;
    private readonly JsonFileOperatorStore _store;
    private readonly FeaturesService _features;
    private readonly OperatorService _operator;
    private readonly CommentService _comments;

    public CommentServiceTests()
    {
        var options = Options.Create(new FeatureStateStoreOptions { FilePath = Path.Combine(_directory, "features.json") });
        _states = new JsonFileFeatureStateStore(options, NullLogger<JsonFileFeatureStateStore>.Instance);
        _actions = new JsonFileActionQueue(options, NullLogger<JsonFileActionQueue>.Instance);
        _writes = new JsonLinesWriteLog(options, NullLogger<JsonLinesWriteLog>.Instance);
        _store = new JsonFileOperatorStore(options, NullLogger<JsonFileOperatorStore>.Instance);
        _features = new FeaturesService(
            new FeatureCatalog(),
            _states,
            new InMemoryCredentialStore(),
            new NullFeatureActivator(),
            Array.Empty<IFeatureCredentialValidator>());
        _operator = new OperatorService(_store, new WriteGateService(new FeatureCatalog(), _states, _actions, _writes), _writes);
        _comments = new CommentService(_store, _operator);
    }

    public void Dispose()
    {
        _states.Dispose();
        _actions.Dispose();
        _writes.Dispose();
        _store.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private Task<FeatureSnapshot> GrantYouTubeAsync() =>
        _features.SetWriteGrantAsync(FeatureCatalog.YouTubeId, granted: true, confirmWrites: true, None);

    private Task<FeatureSnapshot> RevokeYouTubeAsync() =>
        _features.SetWriteGrantAsync(FeatureCatalog.YouTubeId, granted: false, confirmWrites: false, None);

    private static AudienceComment Comment(
        string id,
        string kind = "question",
        string status = "new",
        string text = "How long did this take?",
        DateTimeOffset? published = null,
        DateTimeOffset? fetched = null) =>
        new(id, $"thread-{id}", "v1", "Why rivers bend", "Ana", text, published ?? Published, 3, kind, "effort", status,
            status == "drafted" ? "About a week." : string.Empty, null, null, null, null, fetched ?? DateTimeOffset.UtcNow);

    private static AudienceIdea Idea(string commentId, string status = "proposed", string text = "How deltas form") =>
        new($"idea-{commentId}", text, commentId, status, null);

    private Task<CommentsState> SeedAsync(IReadOnlyList<AudienceComment> comments, IReadOnlyList<AudienceIdea>? ideas = null) =>
        _store.UpdateCommentsAsync(_ => new CommentsState(comments, ideas ?? [], null, null, null), None);

    private async Task<AudienceComment> StoredAsync(string id) =>
        (await _store.ReadCommentsAsync(None)).Comments.Single(c => c.Id == id);

    private async Task<CommentsState> WorkerSavesAsync(Func<CommentsState, CommentsState> change) =>
        await _comments.SaveCommentsFromWorkerAsync(change(await _comments.GetCommentsForWorkerAsync(None)), None);

    private static CommentsState With(CommentsState state, string id, Func<AudienceComment, AudienceComment> change) =>
        state with { Comments = [.. state.Comments.Select(c => c.Id == id ? change(c) : c)] };

    private async Task<List<PendingAction>> ApprovedActionsAsync() =>
        [.. await Task.WhenAll((await _writes.ListAsync(100, None))
            .Where(e => e.Outcome == WriteGateService.ApprovedOutcome)
            .Reverse()
            .Select(async e => (await _actions.FindAsync(e.ActionId!, None)).ShouldNotBeNull()))];

    [Fact]
    public async Task Hold_on_a_quarantined_comment_enqueues_one_approved_setModerationStatus()
    {
        await GrantYouTubeAsync();
        var text = "Win a free phone at example.test " + new string('x', 150);
        await SeedAsync([Comment("c1", kind: "spam", status: "quarantined", text: text)]);
        var before = DateTimeOffset.UtcNow;

        var held = await _comments.DecideCommentAsync("c1", "hold", None);

        var comment = held.Comments.ShouldHaveSingleItem();
        comment.Status.ShouldBe("hold_approved");
        comment.DecidedAt.ShouldNotBeNull().ShouldBeInRange(before, DateTimeOffset.UtcNow);
        (await StoredAsync("c1")).ShouldBe(comment);
        var action = (await ApprovedActionsAsync()).ShouldHaveSingleItem();
        (action.FeatureId, action.Action, action.Target, action.Status)
            .ShouldBe(("youtube", "comments.setModerationStatus", "comment:c1", PendingActionStatus.Approved));
        action.Summary.ShouldBe($"Hold for review on YouTube: Ana on \"Why rivers bend\": \"{text[..120]}\"");
        (await _writes.ListAsync(100, None)).Single(e => e.ActionId == action.Id).ApprovedBy.ShouldBe("owner");
    }

    [Theory]
    [InlineData("new")]
    [InlineData("held")]
    [InlineData("dismissed")]
    public async Task Hold_is_refused_on_a_comment_that_is_not_quarantined(string status)
    {
        await GrantYouTubeAsync();
        await SeedAsync([Comment("c1", kind: "spam", status: status)]);

        var ex = await Should.ThrowAsync<OperatorValidationException>(() => _comments.DecideCommentAsync("c1", "hold", None));

        ex.Message.ShouldBe($"Only a quarantined comment is held for review on YouTube; this one is {status}.");
        (await StoredAsync("c1")).Status.ShouldBe(status);
        (await ApprovedActionsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task ApproveReply_enqueues_one_approved_comments_insert_carrying_the_owner_text()
    {
        await GrantYouTubeAsync();
        await SeedAsync([Comment("c1", status: "drafted")]);
        var reply = "About a week, thanks for asking.\n" + new string('a', 250);

        var approved = await _comments.ApproveReplyAsync("c1", "  " + reply + "  ", None);

        var comment = approved.Comments.ShouldHaveSingleItem();
        (comment.Status, comment.ApprovedText, comment.Draft).ShouldBe(("reply_approved", reply, "About a week."));
        comment.DecidedAt.ShouldNotBeNull();
        var action = (await ApprovedActionsAsync()).ShouldHaveSingleItem();
        (action.FeatureId, action.Action, action.Target, action.Status)
            .ShouldBe(("youtube", "comments.insert", "comment:c1", PendingActionStatus.Approved));
        action.Summary.ShouldBe($"Reply to Ana on \"Why rivers bend\": \"{reply[..200]}\"");
    }

    [Theory]
    [InlineData("   ", "A reply is 1 to 1000 characters.")]
    [InlineData("Thanks\tfor watching", "A reply carries no control character other than a new line.")]
    [InlineData("Thanks\r\nfor watching", "A reply carries no control character other than a new line.")]
    public async Task A_reply_that_is_empty_or_carries_a_control_character_is_refused(string text, string message)
    {
        await GrantYouTubeAsync();
        await SeedAsync([Comment("c1", status: "drafted")]);

        var ex = await Should.ThrowAsync<OperatorValidationException>(() => _comments.ApproveReplyAsync("c1", text, None));

        ex.Message.ShouldBe(message);
        (await StoredAsync("c1")).Status.ShouldBe("drafted");
        (await ApprovedActionsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_reply_to_a_quarantined_comment_or_one_already_replied_is_refused()
    {
        await GrantYouTubeAsync();
        await SeedAsync([Comment("c1", kind: "spam", status: "quarantined"), Comment("c2", status: "replied")]);

        var quarantined = await Should.ThrowAsync<OperatorValidationException>(() => _comments.ApproveReplyAsync("c1", "Thanks", None));
        var replied = await Should.ThrowAsync<OperatorValidationException>(() => _comments.ApproveReplyAsync("c2", "Thanks", None));

        quarantined.Message.ShouldBe("A quarantined comment gets no reply; mark it not spam first.");
        replied.Message.ShouldBe("This comment already has a reply on YouTube.");
        (await ApprovedActionsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Worker_save_that_changes_the_approved_text_is_refused()
    {
        await GrantYouTubeAsync();
        await SeedAsync([Comment("c1", status: "drafted")]);
        await _comments.ApproveReplyAsync("c1", "About a week.", None);

        var ex = await Should.ThrowAsync<OperatorTamperingException>(() =>
            WorkerSavesAsync(s => With(s, "c1", c => c with { Status = "replying", ApprovedText = "Buy my course." })));

        ex.Message.ShouldBe("Only the owner sets the approved reply of comment 'c1'.");
        var stored = await StoredAsync("c1");
        (stored.Status, stored.ApprovedText).ShouldBe(("reply_approved", "About a week."));
    }

    [Fact]
    public async Task Approving_again_supersedes_the_earlier_reply_action()
    {
        await GrantYouTubeAsync();
        await SeedAsync([Comment("c1", status: "drafted")]);
        await _comments.ApproveReplyAsync("c1", "About a week.", None);

        var again = await _comments.ApproveReplyAsync("c1", "About ten days, in the end.", None);

        again.Comments.ShouldHaveSingleItem().ApprovedText.ShouldBe("About ten days, in the end.");
        var actions = await ApprovedActionsAsync();
        actions.Select(a => (a.Action, a.Target, a.Status, a.Reason)).ShouldBe([
            ("comments.insert", "comment:c1", PendingActionStatus.Rejected, "superseded by a new reply"),
            ("comments.insert", "comment:c1", PendingActionStatus.Approved, null),
        ]);
        actions[1].Summary.ShouldBe("Reply to Ana on \"Why rivers bend\": \"About ten days, in the end.\"");
        (await _writes.ListAsync(100, None)).Count(e => e.Outcome == "rejected: superseded by a new reply" && e.ActionId == actions[0].Id)
            .ShouldBe(1);
    }

    [Fact]
    public async Task The_worker_runs_an_approved_reply_to_replied_and_after_a_revoke_only_while_it_executes()
    {
        await GrantYouTubeAsync();
        await SeedAsync([Comment("c1", status: "drafted"), Comment("c2", status: "drafted")]);
        await _comments.ApproveReplyAsync("c1", "About a week.", None);
        (await _operator.NextApprovedActionAsync(None)).ShouldNotBeNull().Target.ShouldBe("comment:c1");
        await WorkerSavesAsync(s => With(s, "c1", c => c with { Status = "replying" }));
        await _operator.CancelApprovedYouTubeActionsAsync(None);
        await RevokeYouTubeAsync();
        var afterRevoke = await _store.ReadCommentsAsync(None);

        var replied = await _comments.SaveCommentsFromWorkerAsync(With(afterRevoke, "c1", c => c with { Status = "replied", ReplyId = "reply-1" }), None);
        var refused = await Should.ThrowAsync<OperatorWriteRefusedException>(() =>
            _comments.SaveCommentsFromWorkerAsync(With(replied, "c2", c => c with { Draft = "Another draft." }), None));

        var stored = replied.Comments.Single(c => c.Id == "c1");
        (stored.Status, stored.ReplyId, stored.ApprovedText).ShouldBe(("replied", "reply-1", "About a week."));
        refused.Message.ShouldBe(NotAllowed);
        (await StoredAsync("c2")).Draft.ShouldBe("About a week.");
    }

    [Theory]
    [InlineData("dismiss", "Only the owner moves comment 'c1' from dismissed to new.")]
    [InlineData("not_spam", "Comment 'c2' carries the owner's decision; the worker may not quarantine it or change its kind.")]
    [InlineData("idea", "Only the owner marks an idea used; idea 'idea-c3' was not.")]
    [InlineData("dismissed_idea", "Idea 'idea-c4' carries the owner's decision; the worker may not change or drop it.")]
    public async Task Worker_cannot_undo_a_dismiss_or_not_spam_or_add_a_decided_idea(string attempt, string message)
    {
        await GrantYouTubeAsync();
        await SeedAsync(
            [Comment("c1"), Comment("c2", kind: "spam", status: "quarantined"), Comment("c3", kind: "request"), Comment("c4", kind: "request")],
            [Idea("c4")]);
        await _comments.DecideCommentAsync("c1", "dismiss", None);
        await _comments.DecideCommentAsync("c2", "not_spam", None);
        await _comments.DecideIdeaAsync("idea-c4", use: false, None);
        var before = await File.ReadAllBytesAsync(_store.CommentsPath);
        Func<CommentsState, CommentsState> change = attempt switch
        {
            "dismiss" => s => With(s, "c1", c => c with { Status = "new" }),
            "not_spam" => s => With(s, "c2", c => c with { Status = "quarantined", Kind = "spam" }),
            "idea" => s => s with { Ideas = [.. s.Ideas, Idea("c3", status: "used")] },
            _ => s => s with { Ideas = [Idea("c4")] },
        };

        var ex = await Should.ThrowAsync<OperatorTamperingException>(() => WorkerSavesAsync(change));

        ex.Message.ShouldBe(message);
        (await File.ReadAllBytesAsync(_store.CommentsPath)).ShouldBe(before);
    }

    [Fact]
    public async Task A_new_comment_is_added_only_as_new_quarantined_or_drafted_and_stamped_with_the_control_planes_fetch_time()
    {
        await GrantYouTubeAsync();
        var before = DateTimeOffset.UtcNow;
        var old = DateTimeOffset.UtcNow.AddDays(-90);

        var saved = await WorkerSavesAsync(s => s with { Comments = [Comment("c1", fetched: old), Comment("c2", kind: "spam", status: "quarantined", fetched: old)] });
        var refused = await Should.ThrowAsync<OperatorTamperingException>(() =>
            WorkerSavesAsync(s => s with { Comments = [.. s.Comments, Comment("c3", status: "replied")] }));

        saved.Comments.Select(c => c.Id).Order().ShouldBe(["c1", "c2"]);
        saved.Comments.ShouldAllBe(c => c.FetchedAt >= before && c.FetchedAt <= DateTimeOffset.UtcNow);
        refused.Message.ShouldBe("A new comment is added as new, quarantined, drafted; comment 'c3' was replied.");
        (await _writes.ListAsync(100, None)).Count(e => e.Outcome == "saved comments" && e.Target == "comments").ShouldBe(1);
    }

    [Fact]
    public async Task RequestCommentSync_is_cleared_by_a_worker_sync_at_or_after_it()
    {
        await GrantYouTubeAsync();

        var requested = (await _comments.RequestCommentSyncAsync(None)).SyncRequestedAt.ShouldNotBeNull();
        var earlier = await WorkerSavesAsync(s => s with { SyncedAt = requested.AddSeconds(-1) });
        var atIt = await WorkerSavesAsync(s => s with { SyncedAt = requested, SyncError = null });

        earlier.SyncRequestedAt.ShouldBe(requested);
        (atIt.SyncedAt, atIt.SyncRequestedAt).ShouldBe(((DateTimeOffset?)requested, (DateTimeOffset?)null));
        (await _store.ReadCommentsAsync(None)).SyncRequestedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Draft_decision_marks_the_comment_draft_requested()
    {
        await SeedAsync([Comment("c1", kind: "praise"), Comment("c2", kind: "spam", status: "dismissed")]);
        var refusedWithoutGrant = await Should.ThrowAsync<OperatorWriteRefusedException>(() => _comments.DecideCommentAsync("c1", "draft", None));
        await GrantYouTubeAsync();

        var requested = await _comments.DecideCommentAsync("c1", "draft", None);
        var spam = await Should.ThrowAsync<OperatorValidationException>(() => _comments.DecideCommentAsync("c2", "draft", None));
        var drafted = await WorkerSavesAsync(s => With(s, "c1", c => c with { Status = "drafted", Draft = "Thank you!" }));

        refusedWithoutGrant.Message.ShouldBe(NotAllowed);
        var comment = requested.Comments.Single(c => c.Id == "c1");
        comment.Status.ShouldBe("draft_requested");
        comment.DecidedAt.ShouldNotBeNull();
        spam.Message.ShouldBe("Only a new, drafted, failed or dismissed comment that is not spam gets a draft; this one is dismissed, spam.");
        drafted.Comments.Single(c => c.Id == "c1").ShouldSatisfyAllConditions(
            c => c.Status.ShouldBe("drafted"),
            c => c.Draft.ShouldBe("Thank you!"));
    }

    [Fact]
    public async Task Using_an_idea_starts_a_project_on_its_text_and_marks_it_used()
    {
        await SeedAsync([Comment("c1", kind: "request"), Comment("c2", kind: "request")], [Idea("c1"), Idea("c2", text: "Glaciers explained")]);
        var dismissed = await _comments.DecideIdeaAsync("idea-c2", use: false, None);
        var refused = await Should.ThrowAsync<OperatorWriteRefusedException>(() => _comments.DecideIdeaAsync("idea-c1", use: true, None));
        await GrantYouTubeAsync();

        var used = await _comments.DecideIdeaAsync("idea-c1", use: true, None);
        var again = await Should.ThrowAsync<OperatorValidationException>(() => _comments.DecideIdeaAsync("idea-c1", use: true, None));

        var project = (await _operator.ListProjectsAsync(0, None)).ShouldHaveSingleItem();
        (project.Topic, project.Status, project.Origin).ShouldBe(("How deltas form", "queued", "desktop"));
        used.Ideas.Single(i => i.Id == "idea-c1").ShouldBe(new AudienceIdea("idea-c1", "How deltas form", "c1", "used", project.Id));
        dismissed.Ideas.Single(i => i.Id == "idea-c2").Status.ShouldBe("dismissed");
        refused.Message.ShouldBe(NotAllowed);
        again.Message.ShouldBe("Idea 'idea-c1' is already used.");
    }

    [Fact]
    public async Task Worker_comment_reads_and_saves_are_refused_without_the_grant()
    {
        var read = await Should.ThrowAsync<OperatorWriteRefusedException>(() => _comments.GetCommentsForWorkerAsync(None));
        var save = await Should.ThrowAsync<OperatorWriteRefusedException>(() =>
            _comments.SaveCommentsFromWorkerAsync(new CommentsState([Comment("c1")], [], DateTimeOffset.UtcNow, null, null), None));

        read.Message.ShouldBe(NotAllowed);
        save.Message.ShouldBe(NotAllowed);
        File.Exists(_store.CommentsPath).ShouldBeFalse();
        (await _writes.ListAsync(100, None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Comments_older_than_30_days_are_cleared()
    {
        var old = DateTimeOffset.UtcNow.AddDays(-31);
        await _actions.EnqueueApprovedAsync(FeatureCatalog.YouTubeId, [("comments.insert", "comment:c2", "Reply")], None);
        await SeedAsync(
            [Comment("c1", fetched: old), Comment("c2", status: "reply_approved", fetched: old) with { ApprovedText = "Thanks." }, Comment("c3")],
            [Idea("c1"), Idea("c3"), Idea("c9", status: "used")]);

        await _operator.ClearOldResearchAsync(None);

        var state = await _store.ReadCommentsAsync(None);
        state.Comments.Select(c => c.Id).ShouldBe(["c2", "c3"]);
        state.Ideas.Select(i => i.Id).ShouldBe(["idea-c3", "idea-c9"]);
    }

    [Fact]
    public async Task Delete_credentials_purges_comments_and_proposed_ideas()
    {
        await _store.UpdateCommentsAsync(
            _ => new CommentsState(
                [Comment("c1"), Comment("c2", status: "replied")],
                [Idea("c1"), Idea("c2", status: "used") with { ProjectId = "p1" }, Idea("c3", status: "dismissed")],
                DateTimeOffset.UtcNow,
                null,
                "quota exceeded"),
            None);

        await _operator.PurgeYouTubeDataAsync(None);

        var state = await _store.ReadCommentsAsync(None);
        state.Comments.ShouldBeEmpty();
        state.Ideas.ShouldBe([new AudienceIdea("idea-c2", string.Empty, "c2", "used", "p1")]);
        (state.SyncedAt, state.SyncError).ShouldBe(((DateTimeOffset?)null, (string?)null));
    }

    [Fact]
    public async Task Revoke_returns_approved_replies_to_drafted_and_holds_to_quarantined()
    {
        await GrantYouTubeAsync();
        await SeedAsync([Comment("c1", status: "drafted"), Comment("c2", kind: "spam", status: "quarantined"), Comment("c3", status: "drafted")]);
        await _comments.ApproveReplyAsync("c3", "Running now.", None);
        (await _operator.NextApprovedActionAsync(None)).ShouldNotBeNull().Target.ShouldBe("comment:c3");
        await _comments.ApproveReplyAsync("c1", "About a week.", None);
        await _comments.DecideCommentAsync("c2", "hold", None);

        await _operator.CancelApprovedYouTubeActionsAsync(None);
        await RevokeYouTubeAsync();

        var state = await _store.ReadCommentsAsync(None);
        state.Comments.Select(c => (c.Id, c.Status, c.ApprovedText)).ShouldBe([
            ("c1", "drafted", (string?)null),
            ("c2", "quarantined", (string?)null),
            ("c3", "reply_approved", "Running now."),
        ]);
    }

    [Fact]
    public async Task Comment_target_has_no_origin_and_is_claimable_beside_a_project_action()
    {
        await GrantYouTubeAsync();
        var projectId = Guid.NewGuid().ToString("N");
        await _actions.EnqueueApprovedAsync(
            FeatureCatalog.YouTubeId,
            [("comments.insert", "comment:c1", "Reply"), ("comments.setModerationStatus", "comment:c2", "Hold"), ("videos.insert", projectId, "Upload")],
            None);

        var first = (await _operator.NextApprovedActionAsync(None)).ShouldNotBeNull();
        var second = (await _operator.NextApprovedActionAsync(None)).ShouldNotBeNull();
        var third = (await _operator.NextApprovedActionAsync(None)).ShouldNotBeNull();

        new[] { first.Target, second.Target, third.Target }.ShouldBe(["comment:c1", "comment:c2", projectId]);
        (await _operator.NextApprovedActionAsync(None)).ShouldBeNull();
        (await _operator.OriginReferenceForWorkerAsync(first, None)).ShouldBeNull();
    }

    [Fact]
    public async Task At_most_1000_comments_are_kept_the_newest_and_never_one_with_an_open_action()
    {
        await GrantYouTubeAsync();
        var oldest = Published.AddDays(-30);
        await _actions.EnqueueApprovedAsync(FeatureCatalog.YouTubeId, [("comments.insert", "comment:open", "Reply")], None);
        await SeedAsync(
            [Comment("open", status: "reply_approved", published: oldest), .. Enumerable.Range(0, 1000).Select(i => Comment($"c{i}", published: Published.AddMinutes(i)))]);

        var saved = await WorkerSavesAsync(s => s with { Comments = [.. s.Comments, Comment("newest", published: Published.AddDays(1))] });

        saved.Comments.Count.ShouldBe(1000);
        saved.Comments.Select(c => c.Id).ShouldContain("open");
        saved.Comments.Select(c => c.Id).ShouldContain("newest");
        saved.Comments.Select(c => c.Id).ShouldNotContain("c0");
        saved.Comments.Select(c => c.Id).ShouldNotContain("c1");
    }

    [Fact]
    public async Task Dismissing_an_approved_reply_or_hold_cancels_its_action()
    {
        await GrantYouTubeAsync();
        await SeedAsync([Comment("c1", status: "drafted"), Comment("c2", kind: "spam", status: "quarantined"), Comment("c3", status: "replying")]);
        await _actions.EnqueueApprovedAsync(FeatureCatalog.YouTubeId, [("comments.insert", "comment:c3", "Reply")], None);
        await _comments.ApproveReplyAsync("c1", "About a week.", None);
        await _comments.DecideCommentAsync("c2", "hold", None);

        var dismissed = await _comments.DecideCommentAsync("c1", "dismiss", None);
        await _comments.DecideCommentAsync("c2", "dismiss", None);
        var replying = await Should.ThrowAsync<OperatorValidationException>(() => _comments.DecideCommentAsync("c3", "dismiss", None));

        dismissed.Comments.Single(c => c.Id == "c1").Status.ShouldBe("dismissed");
        (await StoredAsync("c2")).Status.ShouldBe("dismissed");
        (await ApprovedActionsAsync()).Select(a => (a.Action, a.Target, a.Status, a.Reason)).ShouldBe([
            ("comments.insert", "comment:c1", PendingActionStatus.Rejected, "dismissed by the owner"),
            ("comments.setModerationStatus", "comment:c2", PendingActionStatus.Rejected, "dismissed by the owner"),
        ]);
        (await _writes.ListAsync(100, None)).Count(e => e.Outcome == "rejected: dismissed by the owner").ShouldBe(2);
        (await _operator.NextApprovedActionAsync(None)).ShouldNotBeNull().Target.ShouldBe("comment:c3");
        replying.Message.ShouldBe("A YouTube action on this comment is running; wait for it to end.");
        (await StoredAsync("c3")).Status.ShouldBe("replying");
    }

    [Fact]
    public async Task Dismissing_a_comment_whose_action_is_executing_is_refused_and_keeps_the_action()
    {
        await GrantYouTubeAsync();
        await SeedAsync([Comment("c1", status: "drafted")]);
        await _comments.ApproveReplyAsync("c1", "About a week.", None);
        var claimed = (await _operator.NextApprovedActionAsync(None)).ShouldNotBeNull();

        var ex = await Should.ThrowAsync<OperatorValidationException>(() => _comments.DecideCommentAsync("c1", "dismiss", None));

        ex.Message.ShouldBe("A YouTube action on this comment is running; wait for it to end.");
        (await StoredAsync("c1")).Status.ShouldBe("reply_approved");
        (await _actions.FindAsync(claimed.Id, None)).ShouldNotBeNull().Status.ShouldBe(PendingActionStatus.Executing);
    }

    [Fact]
    public async Task A_failed_sync_answers_the_sync_request()
    {
        await GrantYouTubeAsync();
        var synced = (await WorkerSavesAsync(s => s with { SyncedAt = DateTimeOffset.UtcNow.AddHours(-1) })).SyncedAt;
        await _comments.RequestCommentSyncAsync(None);

        var failed = await WorkerSavesAsync(s => s with { SyncError = "YouTube answered 403: quota exceeded." });
        var requestedAgain = await _comments.RequestCommentSyncAsync(None);
        var failedAgain = await WorkerSavesAsync(s => s with { SyncError = "YouTube did not answer in time." });

        (failed.SyncRequestedAt, failed.SyncedAt, failed.SyncError).ShouldBe(((DateTimeOffset?)null, synced, "YouTube answered 403: quota exceeded."));
        requestedAgain.SyncRequestedAt.ShouldNotBeNull();
        requestedAgain.SyncError.ShouldBe("YouTube answered 403: quota exceeded.");
        (failedAgain.SyncRequestedAt, failedAgain.SyncedAt, failedAgain.SyncError).ShouldBe(((DateTimeOffset?)null, synced, "YouTube did not answer in time."));
    }

    [Fact]
    public async Task A_comment_left_replying_after_a_revoke_and_regrant_is_failed_and_can_be_dismissed()
    {
        await GrantYouTubeAsync();
        await SeedAsync([Comment("c1", status: "drafted"), Comment("c2", status: "drafted")]);
        await _comments.ApproveReplyAsync("c1", "About a week.", None);
        await _comments.ApproveReplyAsync("c2", "Thanks for watching.", None);
        (await _operator.NextApprovedActionAsync(None)).ShouldNotBeNull().Target.ShouldBe("comment:c1");
        (await _operator.NextApprovedActionAsync(None)).ShouldNotBeNull().Target.ShouldBe("comment:c2");
        await WorkerSavesAsync(s => With(With(s, "c1", c => c with { Status = "replying" }), "c2", c => c with { Status = "replying" }));
        await _operator.CancelApprovedYouTubeActionsAsync(None);
        await RevokeYouTubeAsync();
        await GrantYouTubeAsync();

        var dismissed = await _comments.DecideCommentAsync("c2", "dismiss", None);
        await _operator.ClearOldResearchAsync(None);

        dismissed.Comments.Single(c => c.Id == "c2").Status.ShouldBe("dismissed");
        var failed = await StoredAsync("c1");
        (failed.Status, failed.Error, failed.ApprovedText).ShouldBe((
            "failed",
            "The reply may already be on YouTube; check the video's comments in YouTube Studio before replying again.",
            "About a week."));
    }

    [Fact]
    public async Task A_retried_revoke_repairs_comments_whose_actions_were_already_rejected()
    {
        await GrantYouTubeAsync();
        await SeedAsync([Comment("c1", status: "drafted"), Comment("c2", kind: "spam", status: "quarantined")]);
        await _comments.ApproveReplyAsync("c1", "About a week.", None);
        await _comments.DecideCommentAsync("c2", "hold", None);
        Directory.CreateDirectory(_store.CommentsPath + ".tmp");

        var failedWrite = await Should.ThrowAsync<OperatorStoreException>(() => _operator.CancelApprovedYouTubeActionsAsync(None));
        var afterFailure = (await StoredAsync("c1")).Status;
        Directory.Delete(_store.CommentsPath + ".tmp");
        await _operator.CancelApprovedYouTubeActionsAsync(None);

        failedWrite.Message.ShouldBe("Scout could not save the comments in its data folder.");
        afterFailure.ShouldBe("reply_approved");
        (await ApprovedActionsAsync()).ShouldAllBe(a => a.Status == PendingActionStatus.Rejected);
        (await _store.ReadCommentsAsync(None)).Comments.Select(c => (c.Id, c.Status, c.ApprovedText)).ShouldBe([
            ("c1", "drafted", (string?)null),
            ("c2", "quarantined", (string?)null),
        ]);
    }

    [Fact]
    public async Task A_comment_approved_without_a_queued_action_is_returned_to_drafted_by_the_clearing()
    {
        await _actions.EnqueueApprovedAsync(FeatureCatalog.YouTubeId, [("comments.insert", "comment:c3", "Reply")], None);
        await SeedAsync([
            Comment("c1", status: "reply_approved") with { ApprovedText = "About a week." },
            Comment("c2", kind: "spam", status: "hold_approved"),
            Comment("c3", status: "reply_approved") with { ApprovedText = "Queued." },
        ]);

        await _operator.ClearOldResearchAsync(None);

        (await _store.ReadCommentsAsync(None)).Comments.Select(c => (c.Id, c.Status, c.ApprovedText)).ShouldBe([
            ("c1", "drafted", (string?)null),
            ("c2", "quarantined", (string?)null),
            ("c3", "reply_approved", "Queued."),
        ]);
    }

    [Fact]
    public async Task A_comment_with_a_reply_on_YouTube_gets_no_second_reply()
    {
        await GrantYouTubeAsync();
        await SeedAsync([Comment("c1", status: "replied") with { ApprovedText = "About a week.", ReplyId = "reply-1" }]);

        var dismissed = await _comments.DecideCommentAsync("c1", "dismiss", None);
        var draft = await Should.ThrowAsync<OperatorValidationException>(() => _comments.DecideCommentAsync("c1", "draft", None));
        var reply = await Should.ThrowAsync<OperatorValidationException>(() => _comments.ApproveReplyAsync("c1", "A second reply.", None));

        dismissed.Comments.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            c => c.Status.ShouldBe("dismissed"),
            c => c.ReplyId.ShouldBe("reply-1"));
        draft.Message.ShouldBe("This comment already has a reply on YouTube.");
        reply.Message.ShouldBe("This comment already has a reply on YouTube.");
        (await StoredAsync("c1")).Status.ShouldBe("dismissed");
        (await ApprovedActionsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Ideas_are_capped_and_lose_their_text_with_their_comment()
    {
        await GrantYouTubeAsync();
        var old = DateTimeOffset.UtcNow.AddDays(-31);
        await SeedAsync(
            [Comment("o1", fetched: old), Comment("o2", fetched: old), Comment("o3", fetched: old), Comment("f1")],
            [Idea("o1", status: "used") with { ProjectId = "p1" }, Idea("o2", status: "dismissed"), Idea("o3"), Idea("f1", status: "dismissed")]);

        await _operator.ClearOldResearchAsync(None);
        var cleared = (await _store.ReadCommentsAsync(None)).Ideas;
        var capped = await WorkerSavesAsync(s => s with
        {
            Comments = [.. s.Comments, .. Enumerable.Range(0, 205).Select(i => Comment($"n{i}", kind: "request", published: Published.AddMinutes(i + 1)))],
            Ideas = [.. s.Ideas, .. Enumerable.Range(0, 205).Select(i => Idea($"n{i}"))],
        });

        cleared.ShouldBe([new AudienceIdea("idea-o1", string.Empty, "o1", "used", "p1"), Idea("f1", status: "dismissed")]);
        capped.Ideas.Count.ShouldBe(200);
        var ids = capped.Ideas.Select(i => i.Id).ToList();
        ids.ShouldContain("idea-o1");
        ids.ShouldContain("idea-n6");
        ids.ShouldContain("idea-n204");
        ids.ShouldNotContain("idea-f1");
        ids.ShouldNotContain("idea-n5");
    }

    [Fact]
    public async Task A_save_without_a_sync_error_keeps_the_stored_error_and_leaves_the_request_open()
    {
        await GrantYouTubeAsync();
        var synced = DateTimeOffset.UtcNow.AddHours(-1);
        await WorkerSavesAsync(s => s with { SyncedAt = synced });
        await WorkerSavesAsync(s => s with { SyncError = "YouTube answered 403: quota exceeded." });
        var requested = (await _comments.RequestCommentSyncAsync(None)).SyncRequestedAt.ShouldNotBeNull();

        var draftsOnly = await WorkerSavesAsync(s => s with { SyncError = null });
        var emptyError = await WorkerSavesAsync(s => s with { SyncError = string.Empty });
        var resynced = await WorkerSavesAsync(s => s with { SyncedAt = requested.AddSeconds(1), SyncError = null });

        (draftsOnly.SyncError, draftsOnly.SyncRequestedAt, draftsOnly.SyncedAt)
            .ShouldBe(("YouTube answered 403: quota exceeded.", (DateTimeOffset?)requested, (DateTimeOffset?)synced));
        (emptyError.SyncError, emptyError.SyncRequestedAt).ShouldBe(("YouTube answered 403: quota exceeded.", (DateTimeOffset?)requested));
        (resynced.SyncError, resynced.SyncRequestedAt, resynced.SyncedAt)
            .ShouldBe(((string?)null, (DateTimeOffset?)null, (DateTimeOffset?)requested.AddSeconds(1)));
    }
}
