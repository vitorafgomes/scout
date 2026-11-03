using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Features;
using Scout.ControlPlane.Operator;
using Scout.ControlPlane.WriteGate;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Operator;

/// <summary>Publishing an approved package, the worker's publication saves, experiments and the approved actions it claims.</summary>
public sealed partial class OperatorServiceTests
{
    private const string VideoId = "vid-1";

    private static readonly DateTimeOffset Tomorrow = DateTimeOffset.UtcNow.AddDays(1);

    private async Task<OperatorProject> ApprovedAsync(Func<VideoPackage, VideoPackage>? change = null)
    {
        var ready = await ReadyToApproveAsync(change);
        return await _operator.ApprovePackageAsync(ready.Id, None);
    }

    private Task<OperatorProject> PublishAsync(string id, string privacy = "public", DateTimeOffset? at = null) =>
        _operator.PublishPackageAsync(id, privacy, at, madeForKids: false, syntheticMedia: true, None);

    private async Task<OperatorProject> WorkerSavesAsync(string id, Func<Publication, Publication> change)
    {
        var read = await _operator.GetProjectForWorkerAsync(id, None);
        return await _operator.SaveProjectFromWorkerAsync(read with { Publication = change(read.Publication!) }, null, None);
    }

    private async Task<OperatorProject> LiveAsync(params Proposal[] proposals)
    {
        var approved = await ApprovedAsync();
        await PublishAsync(approved.Id);
        await WorkerSavesAsync(approved.Id, p => p with { Status = "published", VideoId = VideoId, Proposals = proposals });
        foreach (var proposal in proposals.Where(p => p.Status == "proposed" && p.Reason == "accept"))
        {
            await _operator.DecideProposalAsync(approved.Id, proposal.Id, accept: true, None);
        }

        return await StoredAsync(approved.Id);
    }

    private static Proposal TitleProposal(string id, string text, string reason = "accept") => new(id, "title", text, reason, "proposed", null);

    private async Task<List<WriteLogEntry>> OwnerApprovalsAsync() =>
        [.. (await _writes.ListAsync(100, None)).Where(e => e.Outcome == "approved").Reverse()];

    private async Task<List<PendingAction>> ApprovedActionsAsync() =>
        [.. await Task.WhenAll((await OwnerApprovalsAsync()).Select(async e => (await _actions.FindAsync(e.ActionId!, None)).ShouldNotBeNull()))];

    private WriteGateService Gate() => new(new FeatureCatalog(), _states, _actions, _writes);

    [Fact]
    public async Task Publishing_Needs_The_Owners_Grant()
    {
        var approved = await ApprovedAsync();
        await RevokeYouTubeAsync();

        var ex = await Should.ThrowAsync<OperatorWriteRefusedException>(() => PublishAsync(approved.Id));

        ex.Message.ShouldBe(NotAllowed);
        (await StoredAsync(approved.Id)).Publication.ShouldBeNull();
        (await OwnerApprovalsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Only_An_Approved_Package_Is_Published()
    {
        var assembled = await ReadyToApproveAsync();

        var ex = await Should.ThrowAsync<OperatorValidationException>(() => PublishAsync(assembled.Id));

        ex.Message.ShouldBe("Only an approved package is published; this one is assembled.");
        (await StoredAsync(assembled.Id)).Publication.ShouldBeNull();
    }

    [Theory]
    [InlineData("secret", 0, "A video is published as public, private or unlisted.")]
    [InlineData("private", -1, "The publish time must be in the future.")]
    [InlineData("public", 1, "A video with a publish time is uploaded as private; YouTube makes it public at that time.")]
    [InlineData("unlisted", 1, "A video with a publish time is uploaded as private; YouTube makes it public at that time.")]
    public async Task The_Privacy_Is_Public_Private_Or_Unlisted_And_A_Publish_Time_Is_Future_And_Private(
        string privacy, int publishInDays, string message)
    {
        var approved = await ApprovedAsync();
        DateTimeOffset? at = publishInDays == 0 ? null : DateTimeOffset.UtcNow.AddDays(publishInDays);

        var ex = await Should.ThrowAsync<OperatorValidationException>(() => PublishAsync(approved.Id, privacy, at));

        ex.Message.ShouldBe(message);
        (await StoredAsync(approved.Id)).Publication.ShouldBeNull();
    }

    [Fact]
    public async Task Publishing_Keeps_The_Owners_Settings_And_Queues_The_Upload_And_The_Thumbnail_As_Approved_And_Logged_By_The_Owner()
    {
        var approved = await ApprovedAsync();

        var published = await _operator.PublishPackageAsync(approved.Id, "private", Tomorrow, madeForKids: true, syntheticMedia: true, None);

        var publication = published.Publication.ShouldNotBeNull();
        (publication.Status, publication.VideoId, publication.PrivacyStatus, publication.PublishAt, publication.MadeForKids, publication.SyntheticMedia)
            .ShouldBe(("publish_approved", (string?)null, "private", (DateTimeOffset?)Tomorrow, true, true));
        JsonSerializer.Serialize((await StoredAsync(approved.Id)).Publication).ShouldBe(JsonSerializer.Serialize(publication));
        var actions = await ApprovedActionsAsync();
        actions.Select(a => (a.FeatureId, a.Action, a.Target, a.Status)).ShouldBe([
            ("youtube", "videos.insert", approved.Id, PendingActionStatus.Approved),
            ("youtube", "thumbnails.set", approved.Id, PendingActionStatus.Approved),
        ]);
        actions[0].Summary.ShouldBe(
            $"Upload \"Why rivers bend\" to YouTube as private, made public at {Tomorrow.UtcDateTime:yyyy-MM-dd HH:mm} UTC.");
        (await OwnerApprovalsAsync()).Select(e => (e.Action, e.Target, e.ApprovedBy, e.ActionId))
            .ShouldBe(actions.Select(a => (a.Action, a.Target, (string?)"owner", (string?)a.Id)));
    }

    [Fact]
    public async Task A_Package_Without_A_Thumbnail_Queues_Only_The_Upload()
    {
        var approved = await ApprovedAsync(p => p with { ThumbnailFile = null });

        await PublishAsync(approved.Id, "unlisted");

        (await ApprovedActionsAsync()).Select(a => (a.Action, a.Summary))
            .ShouldBe([("videos.insert", "Upload \"Why rivers bend\" to YouTube as unlisted.")]);
    }

    [Fact]
    public async Task A_Failed_Upload_Without_A_Video_Is_Published_Again_And_A_Video_On_YouTube_Is_Not()
    {
        var approved = await ApprovedAsync();
        await PublishAsync(approved.Id);
        await WorkerSavesAsync(approved.Id, p => p with { Status = "failed", Error = "quota exceeded" });

        var again = await PublishAsync(approved.Id, "unlisted");
        await WorkerSavesAsync(approved.Id, p => p with { Status = "uploaded", VideoId = VideoId });
        var ex = await Should.ThrowAsync<OperatorValidationException>(() => PublishAsync(approved.Id));

        (again.Publication!.Status, again.Publication.PrivacyStatus, again.Publication.Error).ShouldBe(("publish_approved", "unlisted", (string?)null));
        (await ApprovedActionsAsync()).Count(a => a.Action == "videos.insert").ShouldBe(2);
        ex.Message.ShouldBe("The video of this project is already on YouTube.");
        (await StoredAsync(approved.Id)).Publication!.PrivacyStatus.ShouldBe("unlisted");
    }

    [Fact]
    public async Task A_Video_Uploading_Now_Is_Not_Published_Again()
    {
        var approved = await ApprovedAsync();
        await PublishAsync(approved.Id);
        await WorkerSavesAsync(approved.Id, p => p with { Status = "uploading" });

        var ex = await Should.ThrowAsync<OperatorValidationException>(() => PublishAsync(approved.Id));

        ex.Message.ShouldBe("The video of this project is uploading; wait for the upload to end.");
        (await ApprovedActionsAsync()).Count.ShouldBe(2);
    }

    [Fact]
    public async Task The_Worker_Saves_The_Status_The_Video_Id_And_The_Upload_Time_Of_An_Approved_Project()
    {
        var approved = await ApprovedAsync();
        await PublishAsync(approved.Id);
        var uploadedAt = DateTimeOffset.UtcNow;

        await WorkerSavesAsync(approved.Id, p => p with { Status = "uploading" });
        var saved = await WorkerSavesAsync(approved.Id, p => p with { Status = "published", VideoId = VideoId, UploadedAt = uploadedAt });

        var stored = await StoredAsync(approved.Id);
        (stored.Status, stored.Publication!.Status, stored.Publication.VideoId, stored.Publication.UploadedAt)
            .ShouldBe(("approved", "published", (string?)VideoId, (DateTimeOffset?)uploadedAt));
        JsonSerializer.Serialize(saved.Publication).ShouldBe(JsonSerializer.Serialize(stored.Publication));
        (await _writes.ListAsync(1, None)).Single().Outcome.ShouldBe("saved approved");
    }

    [Theory]
    [InlineData("privacy")]
    [InlineData("publish time")]
    [InlineData("made for kids")]
    [InlineData("synthetic media")]
    public async Task The_Worker_Cannot_Change_The_Owners_Publish_Settings(string setting)
    {
        var approved = await ApprovedAsync();
        await _operator.PublishPackageAsync(approved.Id, "private", Tomorrow, madeForKids: false, syntheticMedia: true, None);

        var ex = await Should.ThrowAsync<OperatorTamperingException>(() => WorkerSavesAsync(approved.Id, p => setting switch
        {
            "privacy" => p with { PrivacyStatus = "public" },
            "publish time" => p with { PublishAt = Tomorrow.AddHours(1) },
            "made for kids" => p with { MadeForKids = true },
            _ => p with { SyntheticMedia = false },
        }));

        ex.Message.ShouldBe($"Only the owner sets the privacy, the publish time and the declarations of project '{approved.Id}'.");
        (await StoredAsync(approved.Id)).Publication!.PrivacyStatus.ShouldBe("private");
    }

    [Fact]
    public async Task Only_The_Owner_Publishes_And_Sets_A_Publication_To_Publish_Approved()
    {
        var approved = await ApprovedAsync();
        var read = await _operator.GetProjectForWorkerAsync(approved.Id, None);
        var unpublished = await Should.ThrowAsync<OperatorTamperingException>(() => _operator.SaveProjectFromWorkerAsync(
            read with { Publication = new Publication("uploading", null, "public", null, false, false, null, null, [], [], null) }, null, None));
        await PublishAsync(approved.Id);
        await WorkerSavesAsync(approved.Id, p => p with { Status = "failed" });

        var reapproved = await Should.ThrowAsync<OperatorTamperingException>(() =>
            WorkerSavesAsync(approved.Id, p => p with { Status = "publish_approved" }));

        unpublished.Message.ShouldBe($"Only the owner publishes project '{approved.Id}'.");
        reapproved.Message.ShouldBe($"Only the owner sets the publication of project '{approved.Id}' to publish_approved.");
        (await StoredAsync(approved.Id)).Publication!.Status.ShouldBe("failed");
    }

    [Fact]
    public async Task The_Worker_Sets_The_Video_Id_Only_While_It_Is_Unset_Or_To_The_Same_Id()
    {
        var approved = await ApprovedAsync();
        await PublishAsync(approved.Id);
        await WorkerSavesAsync(approved.Id, p => p with { Status = "uploaded", VideoId = VideoId });

        await WorkerSavesAsync(approved.Id, p => p with { Status = "published", VideoId = VideoId });
        var ex = await Should.ThrowAsync<OperatorTamperingException>(() => WorkerSavesAsync(approved.Id, p => p with { VideoId = "vid-2" }));

        ex.Message.ShouldBe($"Project '{approved.Id}' is on YouTube as '{VideoId}'; the worker may not change its video id.");
        (await StoredAsync(approved.Id)).Publication!.VideoId.ShouldBe(VideoId);
    }

    [Fact]
    public async Task Snapshots_Are_Added_Or_Replaced_By_Their_Window()
    {
        var project = await LiveAsync();
        Snapshot Taken(string window, long views) =>
            new(window, DateTimeOffset.UtcNow, views, 1, 30, 40, 1, 0, 2, 3, 4, null, null, [new RetentionPoint(0.5, 0.6)]);

        await WorkerSavesAsync(project.Id, p => p with { Snapshots = [Taken("24h", 100)] });
        await WorkerSavesAsync(project.Id, p => p with { Snapshots = [Taken("7d", 700)] });
        await WorkerSavesAsync(project.Id, p => p with { Snapshots = [Taken("24h", 150)] });

        (await StoredAsync(project.Id)).Publication!.Snapshots.Select(s => (s.Window, s.Views)).ShouldBe([("7d", 700L), ("24h", 150L)]);
    }

    [Fact]
    public async Task The_Worker_Adds_Proposals_Only_As_Proposed_And_Never_Changes_Or_Drops_A_Decided_One()
    {
        var project = await LiveAsync();
        var forged = await Should.ThrowAsync<OperatorTamperingException>(() =>
            WorkerSavesAsync(project.Id, p => p with { Proposals = [TitleProposal("t1", "Rivers bend") with { Status = "accepted" }] }));
        await WorkerSavesAsync(project.Id, p => p with { Proposals = [TitleProposal("t1", "Rivers bend")] });
        await _operator.DecideProposalAsync(project.Id, "t1", accept: true, None);

        var changed = await Should.ThrowAsync<OperatorTamperingException>(() =>
            WorkerSavesAsync(project.Id, p => p with { Proposals = [p.Proposals[0] with { Text = "Rivers wander" }] }));
        var dropped = await Should.ThrowAsync<OperatorTamperingException>(() => WorkerSavesAsync(project.Id, p => p with { Proposals = [] }));

        forged.Message.ShouldBe("Only the owner marks a proposal accepted; proposal 't1' was not.");
        changed.Message.ShouldBe("Proposal 't1' carries the owner's decision; the worker may not change or drop it.");
        dropped.Message.ShouldBe(changed.Message);
        (await StoredAsync(project.Id)).Publication!.Proposals.ShouldBe([TitleProposal("t1", "Rivers bend") with { Status = "accepted" }]);
    }

    [Fact]
    public async Task A_Save_On_An_Approved_Project_That_Changes_More_Than_The_Publication_Is_Refused()
    {
        var approved = await ApprovedAsync();
        await PublishAsync(approved.Id);
        var read = await _operator.GetProjectForWorkerAsync(approved.Id, None);

        var ex = await Should.ThrowAsync<OperatorTamperingException>(() => _operator.SaveProjectFromWorkerAsync(
            read with { Error = "rewritten", Publication = read.Publication! with { Status = "uploading" } }, null, None));

        ex.Message.ShouldBe("The project is no longer assembling.");
        var stored = await StoredAsync(approved.Id);
        (stored.Error, stored.Publication!.Status).ShouldBe(((string?)null, "publish_approved"));
    }

    private async Task<OperatorProject> WithResearchAsync(TimeSpan age)
    {
        var approved = await ApprovedAsync();
        await SetStoredAsync(approved.Id, p => p with
        {
            CreatedAt = DateTimeOffset.UtcNow - age,
            Candidates = [new TopicCandidate("Rivers", "Fits the pillars", 0.9)],
            Claims = [new Claim("c1", "Rivers meander.", "low", ["https://example.org/rivers"], "supported", null, DateTimeOffset.UtcNow)],
        });
        return await _operator.GetProjectForWorkerAsync(approved.Id, None);
    }

    [Fact]
    public async Task The_Worker_Clears_Claim_Sources_Older_Than_30_Days_But_Never_The_Topic_Candidates()
    {
        var read = await WithResearchAsync(TimeSpan.FromDays(31));

        await _operator.SaveProjectFromWorkerAsync(read with { Claims = [read.Claims[0] with { SourceUrls = [] }] }, null, None);
        var candidates = await Should.ThrowAsync<OperatorTamperingException>(() =>
            _operator.SaveProjectFromWorkerAsync(read with { Candidates = [], Claims = [read.Claims[0] with { SourceUrls = [] }] }, null, None));

        candidates.Message.ShouldBe("The project is no longer assembling.");
        var stored = await StoredAsync(read.Id);
        (stored.Status, stored.Candidates.Count, stored.Claims.Single().SourceUrls.Count, stored.Claims.Single().Status)
            .ShouldBe(("approved", 1, 0, "supported"));
    }

    [Fact]
    public async Task Research_Younger_Than_30_Days_Is_Not_Cleared_By_The_Worker()
    {
        var read = await WithResearchAsync(TimeSpan.FromDays(29));

        var sources = await Should.ThrowAsync<OperatorTamperingException>(() =>
            _operator.SaveProjectFromWorkerAsync(read with { Claims = [read.Claims[0] with { SourceUrls = [] }] }, null, None));
        var candidates = await Should.ThrowAsync<OperatorTamperingException>(() =>
            _operator.SaveProjectFromWorkerAsync(read with { Candidates = [] }, null, None));

        sources.Message.ShouldBe("Claim 'c1' carries the owner's decision; the worker may not change or drop it.");
        candidates.Message.ShouldBe("The project is no longer assembling.");
        var stored = await StoredAsync(read.Id);
        (stored.Candidates.Count, stored.Claims.Single().SourceUrls.Count).ShouldBe((1, 1));
    }

    [Fact]
    public async Task The_Owner_Accepts_Or_Rejects_A_Proposal_Once()
    {
        var project = await LiveAsync(TitleProposal("t1", "Rivers bend", "keep"), TitleProposal("t2", "Rivers wander", "keep"));

        await _operator.DecideProposalAsync(project.Id, "t1", accept: true, None);
        var decided = await _operator.DecideProposalAsync(project.Id, "t2", accept: false, None);
        var again = await Should.ThrowAsync<OperatorValidationException>(() => _operator.DecideProposalAsync(project.Id, "t1", accept: false, None));
        var unknown = await Should.ThrowAsync<OperatorNotFoundException>(() => _operator.DecideProposalAsync(project.Id, "t9", accept: true, None));

        decided.Publication!.Proposals.Select(p => (p.Id, p.Status)).ShouldBe([("t1", "accepted"), ("t2", "rejected")]);
        again.Message.ShouldBe("Proposal 't1' is already accepted.");
        unknown.Message.ShouldBe($"Unknown proposal 't9' in project '{project.Id}'.");
    }

    [Fact]
    public async Task An_Experiment_Needs_A_Published_Or_Scheduled_Video_And_An_Accepted_Title_Or_Thumbnail_Proposal()
    {
        var approved = await ApprovedAsync();
        await PublishAsync(approved.Id);
        await WorkerSavesAsync(approved.Id, p => p with { Status = "uploaded", VideoId = VideoId, Proposals = [TitleProposal("t1", "Rivers bend")] });

        var uploaded = await Should.ThrowAsync<OperatorValidationException>(() => _operator.StartExperimentAsync(approved.Id, 0, None));
        await WorkerSavesAsync(approved.Id, p => p with { Status = "scheduled" });
        var undecided = await Should.ThrowAsync<OperatorValidationException>(() => _operator.StartExperimentAsync(approved.Id, 0, None));

        uploaded.Message.ShouldBe("Only a published or scheduled video is tested; this one is uploaded.");
        undecided.Message.ShouldBe("Accept a title or thumbnail proposal first.");
        (await StoredAsync(approved.Id)).Publication!.Experiment.ShouldBeNull();
        (await OwnerApprovalsAsync()).Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(23)]
    [InlineData(169)]
    public async Task Each_Arm_Runs_24_To_168_Hours(int hours)
    {
        var project = await LiveAsync(TitleProposal("t1", "Rivers bend"));

        var ex = await Should.ThrowAsync<OperatorValidationException>(() => _operator.StartExperimentAsync(project.Id, hours, None));

        ex.Message.ShouldBe("Each arm runs 24 to 168 hours.");
        (await StoredAsync(project.Id)).Publication!.Experiment.ShouldBeNull();
    }

    [Fact]
    public async Task A_Title_Experiment_Has_A_Control_And_Up_To_Two_Variants_And_Queues_Each_Switch_And_The_Restore_As_Approved()
    {
        var project = await LiveAsync(
            TitleProposal("t1", "Rivers bend"),
            new Proposal("th1", "thumbnail", "Blue river", "accept", "proposed", "river.png"),
            TitleProposal("t2", "Rivers wander"),
            TitleProposal("t3", "Rivers loop"));

        var started = await _operator.StartExperimentAsync(project.Id, 0, None);

        var experiment = started.Publication!.Experiment.ShouldNotBeNull();
        (experiment.Kind, experiment.Status, experiment.ArmHours).ShouldBe(("title", "running", 48));
        experiment.Arms.Select(a => (a.Id, a.Label, a.Value)).ShouldBe([
            ("control", "Control", "Why rivers bend"),
            ("variant-a", "Variant A", "Rivers bend"),
            ("variant-b", "Variant B", "Rivers wander"),
        ]);
        var actions = (await ApprovedActionsAsync()).Skip(2).ToList();
        var prefix = $"{project.Id}:{experiment.Id}";
        actions.Select(a => (a.Action, a.Target, a.Status)).ShouldBe([
            ("videos.update", $"{prefix}:control", PendingActionStatus.Approved),
            ("videos.update", $"{prefix}:variant-a", PendingActionStatus.Approved),
            ("videos.update", $"{prefix}:variant-b", PendingActionStatus.Approved),
            ("videos.update", $"{prefix}:control", PendingActionStatus.Approved),
        ]);
        actions[1].Summary.ShouldBe("Change the video's title to \"Rivers bend\" for 48 hours, Variant A of a title test.");
        actions[3].Summary.ShouldBe("Change the video's title to \"Why rivers bend\" again when the title test ends.");
    }

    [Fact]
    public async Task A_Thumbnail_Experiment_Switches_Thumbnails_From_The_Packages_One()
    {
        var project = await LiveAsync(new Proposal("th1", "thumbnail", "Blue river", "accept", "proposed", "river.png"));

        var started = await _operator.StartExperimentAsync(project.Id, 24, None);

        started.Publication!.Experiment!.Arms.Select(a => a.Value).ShouldBe(["thumb.png", "river.png"]);
        (await ApprovedActionsAsync()).Skip(2).Select(a => a.Action).ShouldBe(["thumbnails.set", "thumbnails.set", "thumbnails.set"]);
    }

    [Fact]
    public async Task A_Second_Experiment_Waits_Until_The_Running_One_Ends()
    {
        var project = await LiveAsync(TitleProposal("t1", "Rivers bend"));
        await _operator.StartExperimentAsync(project.Id, 0, None);

        var ex = await Should.ThrowAsync<OperatorValidationException>(() => _operator.StartExperimentAsync(project.Id, 0, None));

        ex.Message.ShouldBe("An experiment is already running on this video.");
        (await ApprovedActionsAsync()).Count.ShouldBe(5);
    }

    [Fact]
    public async Task The_Worker_Measures_And_Ends_A_Running_Experiment_But_Never_Changes_Its_Arms()
    {
        var project = await LiveAsync(TitleProposal("t1", "Rivers bend"));
        await _operator.StartExperimentAsync(project.Id, 0, None);
        var started = DateTimeOffset.UtcNow.AddDays(-4);

        var relabelled = await Should.ThrowAsync<OperatorTamperingException>(() => WorkerSavesAsync(project.Id, p => p with
        {
            Experiment = p.Experiment! with { Arms = [p.Experiment.Arms[0], p.Experiment.Arms[1] with { Value = "Rivers loop" }] },
        }));
        await WorkerSavesAsync(project.Id, p => p with
        {
            Experiment = p.Experiment! with
            {
                Status = "concluded",
                WinnerArmId = "variant-a",
                Result = "Variant A wins.",
                Arms = [.. p.Experiment.Arms.Select(a => a with { StartedAt = started, EndedAt = started.AddDays(2), Impressions = 2000, Clicks = 90 })],
            },
        });
        var over = await Should.ThrowAsync<OperatorTamperingException>(() =>
            WorkerSavesAsync(project.Id, p => p with { Experiment = p.Experiment! with { WinnerArmId = "control" } }));

        relabelled.Message.ShouldBe($"Only the owner starts an experiment on project '{project.Id}' and chooses its arms.");
        over.Message.ShouldBe($"The experiment of project '{project.Id}' is over; the worker may not change it.");
        var experiment = (await StoredAsync(project.Id)).Publication!.Experiment!;
        (experiment.Status, experiment.WinnerArmId, experiment.ArmHours, experiment.Arms[1].Value, experiment.Arms[1].Clicks)
            .ShouldBe(("concluded", "variant-a", 48, "Rivers bend", 90L));
    }

    [Fact]
    public async Task Only_A_Concluded_Experiment_With_A_Winner_Is_Adopted_With_One_Approved_Action()
    {
        var project = await LiveAsync(TitleProposal("t1", "Rivers bend"));
        await _operator.StartExperimentAsync(project.Id, 0, None);
        await WorkerSavesAsync(project.Id, p => p with { Experiment = p.Experiment! with { WinnerArmId = "variant-a" } });
        var running = await Should.ThrowAsync<OperatorValidationException>(() => _operator.AdoptExperimentWinnerAsync(project.Id, None));
        await WorkerSavesAsync(project.Id, p => p with { Experiment = p.Experiment! with { Status = "concluded" } });

        await _operator.AdoptExperimentWinnerAsync(project.Id, None);

        running.Message.ShouldBe("Only a concluded experiment with a winner is adopted.");
        var experiment = (await StoredAsync(project.Id)).Publication!.Experiment!;
        var adopted = (await ApprovedActionsAsync()).Last();
        (adopted.Action, adopted.Target, adopted.Status, adopted.Summary).ShouldBe((
            "videos.update",
            $"{project.Id}:{experiment.Id}:variant-a",
            PendingActionStatus.Approved,
            "Change the video's title to \"Rivers bend\" for good, the winner of the title test."));
        (await ApprovedActionsAsync()).Count.ShouldBe(6);
    }

    [Fact]
    public async Task An_Adopted_Title_Becomes_The_Control_Of_The_Next_Experiment_And_What_Its_Restore_Puts_Back()
    {
        var project = await LiveAsync(TitleProposal("t1", "Rivers bend"));
        await _operator.StartExperimentAsync(project.Id, 0, None);
        await WorkerSavesAsync(project.Id, p => p with { Experiment = p.Experiment! with { Status = "concluded", WinnerArmId = "variant-a" } });

        await _operator.AdoptExperimentWinnerAsync(project.Id, None);
        await WorkerSavesAsync(project.Id, p => p with { Proposals = [.. p.Proposals, TitleProposal("t2", "Rivers wander")] });
        await _operator.DecideProposalAsync(project.Id, "t2", accept: true, None);
        var next = await _operator.StartExperimentAsync(project.Id, 0, None);

        (await StoredAsync(project.Id)).Package!.Title.ShouldBe("Rivers bend");
        next.Publication!.Experiment!.Arms[0].Value.ShouldBe("Rivers bend");
        (await ApprovedActionsAsync()).Last().Summary.ShouldBe("Change the video's title to \"Rivers bend\" again when the title test ends.");
    }

    [Fact]
    public async Task An_Adopted_Thumbnail_Becomes_The_Packages_Thumbnail()
    {
        var project = await LiveAsync(new Proposal("th1", "thumbnail", "Blue river", "accept", "proposed", "river.png"));
        await _operator.StartExperimentAsync(project.Id, 0, None);
        await WorkerSavesAsync(project.Id, p => p with { Experiment = p.Experiment! with { Status = "concluded", WinnerArmId = "variant-a" } });

        await _operator.AdoptExperimentWinnerAsync(project.Id, None);

        var stored = await StoredAsync(project.Id);
        (stored.Package!.ThumbnailFile, stored.Package.Title).ShouldBe(("river.png", "Why rivers bend"));
    }

    [Fact]
    public async Task Without_The_Grant_No_Approved_Action_Is_Handed_Out()
    {
        var approved = await ApprovedAsync();
        await PublishAsync(approved.Id);
        await RevokeYouTubeAsync();

        (await _operator.NextApprovedActionAsync(None)).ShouldBeNull();

        (await ApprovedActionsAsync()).Select(a => a.Status).ShouldBe([PendingActionStatus.Approved, PendingActionStatus.Approved]);
    }

    [Fact]
    public async Task The_Oldest_Approved_Action_Is_Claimed_Once_As_Executing_And_The_Next_Of_Its_Project_Waits_For_It()
    {
        var first = await ApprovedAsync();
        await PublishAsync(first.Id);
        var second = await ApprovedAsync();
        await PublishAsync(second.Id);
        var before = DateTimeOffset.UtcNow;

        var claimed = (await _operator.NextApprovedActionAsync(None)).ShouldNotBeNull();
        var next = (await _operator.NextApprovedActionAsync(None)).ShouldNotBeNull();
        var none = await _operator.NextApprovedActionAsync(None);

        (claimed.Action, claimed.Target, claimed.Status).ShouldBe(("videos.insert", first.Id, PendingActionStatus.Executing));
        claimed.ClaimedAt.ShouldNotBeNull().ShouldBeInRange(before, DateTimeOffset.UtcNow);
        (await _actions.FindAsync(claimed.Id, None))!.Status.ShouldBe(PendingActionStatus.Executing);
        (next.Action, next.Target).ShouldBe(("videos.insert", second.Id));
        none.ShouldBeNull();
    }

    [Fact]
    public async Task A_Claim_Older_Than_30_Minutes_Is_Handed_Out_Again()
    {
        var approved = await ApprovedAsync();
        await PublishAsync(approved.Id);
        var features = Path.Combine(_directory, "features.json");
        var state = JsonNode.Parse(await File.ReadAllTextAsync(features))!;
        state["youtube"]!["grantedAt"] = DateTimeOffset.UtcNow.AddHours(-2).ToString("O");
        await File.WriteAllTextAsync(features, state.ToJsonString());
        var claimed = (await _operator.NextApprovedActionAsync(None)).ShouldNotBeNull();
        var fresh = await _operator.NextApprovedActionAsync(None);
        await AgeClaimAsync(claimed.Id, TimeSpan.FromMinutes(31));

        var again = (await _operator.NextApprovedActionAsync(None)).ShouldNotBeNull();

        fresh.ShouldBeNull();
        (again.Id, again.Status).ShouldBe((claimed.Id, PendingActionStatus.Executing));
        again.ClaimedAt.ShouldNotBeNull().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task An_Experiment_Arm_Is_Handed_Out_Only_Once_The_Live_Arm_Has_Run_Its_Hours()
    {
        var project = await LiveAsync(TitleProposal("t1", "Rivers bend"));
        foreach (var upload in await ApprovedActionsAsync())
        {
            await _actions.MarkExecutedAsync(upload.Id, None);
        }
        await _operator.StartExperimentAsync(project.Id, 24, None);
        var control = (await _operator.NextApprovedActionAsync(None)).ShouldNotBeNull();
        await Gate().RecordAsync("youtube", control.Action, control.Target, "succeeded", control.Id, None);
        async Task<PendingAction?> LiveForAsync(TimeSpan live)
        {
            await WorkerSavesAsync(project.Id, p => p with
            {
                Experiment = p.Experiment! with { Arms = [p.Experiment.Arms[0] with { StartedAt = DateTimeOffset.UtcNow - live }, p.Experiment.Arms[1]] },
            });
            return await _operator.NextApprovedActionAsync(None);
        }

        var early = await LiveForAsync(TimeSpan.FromHours(23));
        var due = await LiveForAsync(TimeSpan.FromHours(25));

        control.Target.ShouldEndWith(":control");
        early.ShouldBeNull();
        due.ShouldNotBeNull().Target.ShouldEndWith(":variant-a");
    }

    [Fact]
    public async Task Recording_A_Claimed_Action_Consumes_It_With_One_Entry_Of_The_Worker_Beside_The_Owners_Approval()
    {
        var approved = await ApprovedAsync();
        await PublishAsync(approved.Id);
        var claimed = (await _operator.NextApprovedActionAsync(None)).ShouldNotBeNull();

        await Gate().RecordAsync("youtube", "videos.insert", approved.Id, "succeeded", claimed.Id, None);
        var twice = await Should.ThrowAsync<WriteNotRecordableException>(() =>
            Gate().RecordAsync("youtube", "videos.insert", approved.Id, "succeeded", claimed.Id, None));

        (await _actions.FindAsync(claimed.Id, None))!.Status.ShouldBe(PendingActionStatus.Executed);
        (await _writes.ListAsync(100, None)).Where(e => e.ActionId == claimed.Id).Select(e => (e.Outcome, e.ApprovedBy))
            .ShouldBe([("succeeded", "owner"), ("approved", "owner")]);
        twice.Message.ShouldBe($"Action '{claimed.Id}' is not approved by the owner.");
    }

    [Fact]
    public async Task Disconnecting_YouTube_Purges_Snapshots_Candidates_Sources_And_Arm_Numbers_And_Keeps_The_Owners_Content()
    {
        var read = await WithResearchAsync(TimeSpan.FromDays(1));
        await PublishAsync(read.Id);
        await WorkerSavesAsync(read.Id, p => p with
        {
            Status = "published",
            VideoId = VideoId,
            Snapshots = [new Snapshot("24h", DateTimeOffset.UtcNow, 100, 1, 30, 40, 1, 0, 2, 3, 4, 500, 0.05, [])],
        });
        var started = DateTimeOffset.UtcNow.AddDays(-3);
        await SetStoredAsync(read.Id, p => p with
        {
            Publication = p.Publication! with
            {
                Experiment = new Experiment(
                    "e1",
                    "title",
                    "concluded",
                    [new ExperimentArm("control", "Control", "Why rivers bend", started, started.AddDays(2), 2000, 80, 41.5)],
                    "control",
                    "Control wins.",
                    48),
            },
        });
        var before = await StoredAsync(read.Id);
        await RevokeYouTubeAsync();

        await _operator.PurgeYouTubeDataAsync(None);

        var after = await StoredAsync(read.Id);
        (after.Candidates.Count, after.Claims.Single().SourceUrls.Count, after.Publication!.Snapshots.Count).ShouldBe((0, 0, 0));
        (after.Claims.Single().Text, after.Publication.VideoId, after.Status).ShouldBe(("Rivers meander.", (string?)VideoId, "approved"));
        after.Scenes.ShouldBe(before.Scenes);
        JsonSerializer.Serialize(after.Package).ShouldBe(JsonSerializer.Serialize(before.Package));
        JsonSerializer.Serialize(after.Script).ShouldBe(JsonSerializer.Serialize(before.Script));
        var experiment = after.Publication.Experiment!;
        (experiment.Id, experiment.Status, experiment.WinnerArmId, experiment.Result).ShouldBe(("e1", "concluded", (string?)"control", (string?)"Control wins."));
        experiment.Arms.ShouldHaveSingleItem().ShouldBe(new ExperimentArm("control", "Control", "Why rivers bend", started, started.AddDays(2), 0, 0, 0));
    }

    private const string Revoked = "The owner revoked YouTube writes before this action ran.";

    [Fact]
    public async Task A_New_Publish_After_A_Failed_Upload_Rejects_The_Earlier_Approved_Actions_And_Its_Upload_Runs_Next()
    {
        var approved = await ApprovedAsync();
        await PublishAsync(approved.Id);
        var upload = (await _operator.NextApprovedActionAsync(None)).ShouldNotBeNull();
        await Gate().RecordAsync("youtube", upload.Action, upload.Target, "failed: quota exceeded", upload.Id, None);
        await WorkerSavesAsync(approved.Id, p => p with { Status = "failed", Error = "quota exceeded" });

        await PublishAsync(approved.Id, "unlisted");
        var next = (await _operator.NextApprovedActionAsync(None)).ShouldNotBeNull();

        var actions = await ApprovedActionsAsync();
        actions.Select(a => (a.Action, a.Status, a.Reason)).ShouldBe([
            ("videos.insert", PendingActionStatus.Executed, (string?)null),
            ("thumbnails.set", PendingActionStatus.Rejected, "superseded by a new publish"),
            ("videos.insert", PendingActionStatus.Executing, null),
            ("thumbnails.set", PendingActionStatus.Approved, null),
        ]);
        next.Id.ShouldBe(actions[2].Id);
        (await _writes.ListAsync(100, None)).Single(e => e.ActionId == actions[1].Id && e.Outcome != "approved").Outcome
            .ShouldBe("rejected: superseded by a new publish");
    }

    [Fact]
    public async Task Publishing_Is_Refused_While_An_Action_Of_The_Project_Is_Running()
    {
        var approved = await ApprovedAsync();
        await PublishAsync(approved.Id);
        await _operator.NextApprovedActionAsync(None);
        await WorkerSavesAsync(approved.Id, p => p with { Status = "failed" });

        var ex = await Should.ThrowAsync<OperatorValidationException>(() => PublishAsync(approved.Id));

        ex.Message.ShouldBe("A YouTube action of this project is running; wait for it to end.");
        (await ApprovedActionsAsync()).Count.ShouldBe(2);
        (await StoredAsync(approved.Id)).Publication!.Status.ShouldBe("failed");
    }

    [Fact]
    public async Task A_Second_Publish_While_The_First_Is_Queued_Is_Refused()
    {
        var approved = await ApprovedAsync();
        await PublishAsync(approved.Id);

        var ex = await Should.ThrowAsync<OperatorValidationException>(() => PublishAsync(approved.Id, "unlisted"));

        ex.Message.ShouldBe("The video of this project is publish_approved; wait for the upload to end.");
        (await ApprovedActionsAsync()).Select(a => a.Status).ShouldBe([PendingActionStatus.Approved, PendingActionStatus.Approved]);
        (await StoredAsync(approved.Id)).Publication!.PrivacyStatus.ShouldBe("public");
    }

    [Fact]
    public async Task Revoking_YouTube_Rejects_Every_Approved_Action_Keeps_The_Running_One_And_Ends_Running_Experiments()
    {
        var project = await LiveAsync(TitleProposal("t1", "Rivers bend"));
        await _operator.StartExperimentAsync(project.Id, 0, None);
        var running = (await _operator.NextApprovedActionAsync(None)).ShouldNotBeNull();

        await _operator.CancelApprovedYouTubeActionsAsync(None);

        var actions = await ApprovedActionsAsync();
        actions.Single(a => a.Id == running.Id).Status.ShouldBe(PendingActionStatus.Executing);
        actions.Where(a => a.Id != running.Id).Select(a => (a.Status, a.Reason)).Distinct()
            .ShouldBe([(PendingActionStatus.Rejected, (string?)Revoked)]);
        actions.Count.ShouldBe(5);
        (await _writes.ListAsync(100, None)).Count(e => e.Outcome == $"rejected: {Revoked}").ShouldBe(4);
        var experiment = (await StoredAsync(project.Id)).Publication!.Experiment!;
        (experiment.Status, experiment.Result).ShouldBe(("inconclusive", (string?)Revoked));
    }

    [Fact]
    public async Task An_Experiment_Uses_Each_Accepted_Proposal_Once_And_Marks_It_Tested()
    {
        var project = await LiveAsync(TitleProposal("t1", "Rivers bend"));
        var first = await _operator.StartExperimentAsync(project.Id, 0, None);
        await WorkerSavesAsync(project.Id, p => p with
        {
            Experiment = p.Experiment! with { Status = "inconclusive" },
            Proposals = [.. p.Proposals, TitleProposal("t2", "Rivers wander")],
        });
        await _operator.DecideProposalAsync(project.Id, "t2", accept: true, None);

        var second = await _operator.StartExperimentAsync(project.Id, 0, None);

        first.Publication!.Proposals.Single().Status.ShouldBe("tested");
        second.Publication!.Experiment!.Arms.Select(a => a.Value).ShouldBe(["Why rivers bend", "Rivers wander"]);
        second.Publication.Proposals.Select(p => (p.Id, p.Status)).ShouldBe([("t1", "tested"), ("t2", "tested")]);
        (await Should.ThrowAsync<OperatorValidationException>(() => StartAgainAfterEndAsync(project.Id))).Message
            .ShouldBe("Accept a title or thumbnail proposal first.");
    }

    private async Task<OperatorProject> StartAgainAfterEndAsync(string id)
    {
        await WorkerSavesAsync(id, p => p with { Experiment = p.Experiment! with { Status = "inconclusive" } });
        return await _operator.StartExperimentAsync(id, 0, None);
    }

    [Fact]
    public async Task The_Newest_Accepted_Proposal_Chooses_What_The_Experiment_Tests()
    {
        var project = await LiveAsync(TitleProposal("t1", "Rivers bend"), new Proposal("th1", "thumbnail", "Blue river", "accept", "proposed", "river.png"));

        var started = await _operator.StartExperimentAsync(project.Id, 0, None);

        started.Publication!.Experiment!.Kind.ShouldBe("thumbnail");
        started.Publication.Proposals.Select(p => (p.Id, p.Status)).ShouldBe([("t1", "accepted"), ("th1", "tested")]);
    }

    [Fact]
    public async Task Only_The_Owner_Marks_A_Proposal_Tested()
    {
        var project = await LiveAsync(TitleProposal("t1", "Rivers bend"));
        await _operator.StartExperimentAsync(project.Id, 0, None);

        var marked = await Should.ThrowAsync<OperatorTamperingException>(() =>
            WorkerSavesAsync(project.Id, p => p with { Proposals = [.. p.Proposals, TitleProposal("t2", "Rivers loop") with { Status = "tested" }] }));
        var reopened = await Should.ThrowAsync<OperatorTamperingException>(() =>
            WorkerSavesAsync(project.Id, p => p with { Proposals = [p.Proposals[0] with { Status = "proposed" }] }));

        marked.Message.ShouldBe("Only the owner marks a proposal tested; proposal 't2' was not.");
        reopened.Message.ShouldBe("Proposal 't1' carries the owner's decision; the worker may not change or drop it.");
    }

    [Fact]
    public async Task A_Winner_Is_Adopted_Once()
    {
        var project = await LiveAsync(TitleProposal("t1", "Rivers bend"));
        await _operator.StartExperimentAsync(project.Id, 0, None);
        await WorkerSavesAsync(project.Id, p => p with { Experiment = p.Experiment! with { Status = "concluded", WinnerArmId = "variant-a" } });

        var adopted = await _operator.AdoptExperimentWinnerAsync(project.Id, None);
        var again = await Should.ThrowAsync<OperatorValidationException>(() => _operator.AdoptExperimentWinnerAsync(project.Id, None));

        adopted.Publication!.Experiment!.Status.ShouldBe("adopted");
        again.Message.ShouldBe("The winner of this experiment is already adopted.");
        (await ApprovedActionsAsync()).Count(a => a.Summary.Contains("for good", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task An_Experiment_That_Ends_Rejects_Its_Arms_Not_Yet_Run_And_Keeps_The_Restore()
    {
        var project = await LiveAsync(TitleProposal("t1", "Rivers bend"), TitleProposal("t2", "Rivers wander"));
        foreach (var upload in await ApprovedActionsAsync())
        {
            await _actions.MarkExecutedAsync(upload.Id, None);
        }
        await _operator.StartExperimentAsync(project.Id, 24, None);
        var control = (await _operator.NextApprovedActionAsync(None)).ShouldNotBeNull();
        await Gate().RecordAsync("youtube", control.Action, control.Target, "succeeded", control.Id, None);

        await WorkerSavesAsync(project.Id, p => p with { Experiment = p.Experiment! with { Status = "inconclusive" } });

        (await ApprovedActionsAsync()).Skip(2).Select(a => (a.Target[(a.Target.LastIndexOf(':') + 1)..], a.Status, a.Reason)).ShouldBe([
            ("control", PendingActionStatus.Executed, (string?)null),
            ("variant-a", PendingActionStatus.Rejected, "The experiment ended before this arm ran."),
            ("variant-b", PendingActionStatus.Rejected, "The experiment ended before this arm ran."),
            ("control", PendingActionStatus.Approved, null),
        ]);
    }

    [Fact]
    public async Task The_Worker_Lists_Up_To_5000_Projects_And_The_Desktop_Up_To_200()
    {
        await GrantYouTubeAsync();
        await _store.UpdateProjectsAsync(
            projects =>
            {
                projects.AddRange(Enumerable.Range(0, 600).Select(i => new OperatorProject(
                    $"p{i}", $"Topic {i}", "queued", "desktop", default, default, [], null, [])));
                return 0;
            },
            None);

        (await _operator.ListProjectsForWorkerAsync(500, None)).Count.ShouldBe(500);
        (await _operator.ListProjectsForWorkerAsync(0, None)).Count.ShouldBe(50);
        (await _operator.ListProjectsAsync(500, None)).Count.ShouldBe(200);
    }

    private async Task AgeClaimAsync(string actionId, TimeSpan age)
    {
        var file = JsonNode.Parse(await File.ReadAllTextAsync(_actions.FilePath))!.AsArray();
        file.Single(a => (string)a!["id"]! == actionId)!["claimedAt"] = (DateTimeOffset.UtcNow - age).ToString("O");
        await File.WriteAllTextAsync(_actions.FilePath, file.ToJsonString());
    }

    [Fact]
    public async Task Actions_Claimed_Or_Approved_Before_A_New_Grant_Are_Never_Handed_Out_And_Never_Block_The_Project()
    {
        var approved = await ApprovedAsync();
        await PublishAsync(approved.Id);
        var upload = (await _operator.NextApprovedActionAsync(None)).ShouldNotBeNull();
        await RevokeYouTubeAsync();
        await Task.Delay(20);
        await GrantYouTubeAsync();
        await AgeClaimAsync(upload.Id, TimeSpan.FromMinutes(31));

        var stale = await _operator.NextApprovedActionAsync(None);
        await WorkerSavesAsync(approved.Id, p => p with { Status = "failed" });
        await PublishAsync(approved.Id, "unlisted");
        var fresh = (await _operator.NextApprovedActionAsync(None)).ShouldNotBeNull();
        await Gate().RecordAsync("youtube", upload.Action, upload.Target, "succeeded", upload.Id, None);

        stale.ShouldBeNull();
        (fresh.Action, fresh.Summary).ShouldBe(("videos.insert", "Upload \"Why rivers bend\" to YouTube as unlisted."));
        (await _actions.FindAsync(upload.Id, None))!.Status.ShouldBe(PendingActionStatus.Executed);
    }

    private async Task<OperatorProject> UploadingWhenRevokedAsync()
    {
        var approved = await ApprovedAsync();
        await PublishAsync(approved.Id);
        await _operator.NextApprovedActionAsync(None);
        await WorkerSavesAsync(approved.Id, p => p with { Status = "uploading" });
        await RevokeYouTubeAsync();
        return approved;
    }

    [Fact]
    public async Task After_A_Revoke_The_Worker_Saves_What_A_Running_Action_Did()
    {
        var approved = await UploadingWhenRevokedAsync();
        var uploadedAt = DateTimeOffset.UtcNow;

        var read = await _operator.GetProjectForWorkerAsync(approved.Id, None);
        await _operator.SaveProjectFromWorkerAsync(
            read with { Publication = read.Publication! with { Status = "uploaded", VideoId = VideoId, UploadedAt = uploadedAt, Error = null } }, null, None);

        var stored = (await StoredAsync(approved.Id)).Publication!;
        (stored.Status, stored.VideoId, stored.UploadedAt).ShouldBe(("uploaded", (string?)VideoId, (DateTimeOffset?)uploadedAt));
    }

    [Fact]
    public async Task After_A_Revoke_Any_Other_Change_Is_Refused()
    {
        var approved = await UploadingWhenRevokedAsync();
        var read = await _operator.GetProjectForWorkerAsync(approved.Id, None);

        var proposal = await Should.ThrowAsync<OperatorWriteRefusedException>(() => _operator.SaveProjectFromWorkerAsync(
            read with { Publication = read.Publication! with { VideoId = VideoId, Proposals = [TitleProposal("t1", "Rivers bend")] } }, null, None));
        var error = await Should.ThrowAsync<OperatorWriteRefusedException>(() => _operator.SaveProjectFromWorkerAsync(
            read with { Error = "rewritten", Publication = read.Publication! with { VideoId = VideoId } }, null, None));

        (proposal.Message, error.Message).ShouldBe((NotAllowed, NotAllowed));
        (await StoredAsync(approved.Id)).Publication!.VideoId.ShouldBeNull();
    }

    [Fact]
    public async Task After_A_Revoke_Without_A_Running_Action_The_Worker_Neither_Reads_Nor_Saves()
    {
        var approved = await ApprovedAsync();
        await PublishAsync(approved.Id);
        var read = await _operator.GetProjectForWorkerAsync(approved.Id, None);
        await _actions.EnqueueAsync("youtube", "videos.update", approved.Id, "Change the title", None);
        await RevokeYouTubeAsync();

        var get = await Should.ThrowAsync<OperatorWriteRefusedException>(() => _operator.GetProjectForWorkerAsync(approved.Id, None));
        var save = await Should.ThrowAsync<OperatorWriteRefusedException>(() => _operator.SaveProjectFromWorkerAsync(
            read with { Publication = read.Publication! with { Status = "uploaded", VideoId = VideoId } }, null, None));

        (get.Message, save.Message).ShouldBe((NotAllowed, NotAllowed));
    }

    [Fact]
    public async Task Old_Topic_Candidates_Are_Cleared_By_The_Control_Plane_Without_A_Grant()
    {
        var old = await WithResearchAsync(TimeSpan.FromDays(31));
        var young = await WithResearchAsync(TimeSpan.FromDays(29));
        await RevokeYouTubeAsync();

        await _operator.ClearOldResearchAsync(None);

        (await StoredAsync(old.Id)).Candidates.ShouldBeEmpty();
        (await StoredAsync(young.Id)).Candidates.Count.ShouldBe(1);
        (await StoredAsync(old.Id)).Claims.Single().SourceUrls.Count.ShouldBe(1);
    }
}
