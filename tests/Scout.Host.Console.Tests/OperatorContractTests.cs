using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using NSubstitute;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Features;
using Scout.ControlPlane.Operator;
using Scout.ControlPlane.WriteGate;
using Scout.Conversation.Abstractions;
using Scout.Operator;
using Scout.Plugin.YouTube;
using Scout.Plugin.YouTube.Abstractions;
using Shouldly;
using Xunit;

namespace Scout.Host.Console.Tests;

/// <summary>
/// What Scout.Operator sends, checked against the control plane that validates it: the statuses, the claim
/// status, the scope of its declared write, the package metadata both sides count and check the same way, and the comment
/// saves of a sync, a drafts run, a reply and a hold.
/// </summary>
public sealed class OperatorContractTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("scout-operator-contract-").FullName;
    private readonly JsonFileFeatureStateStore _states;
    private readonly JsonFileActionQueue _actions;
    private readonly JsonLinesWriteLog _writes;
    private readonly JsonFileOperatorStore _store;

    public OperatorContractTests()
    {
        var options = Options.Create(new FeatureStateStoreOptions { FilePath = Path.Combine(_directory, "features.json") });
        _states = new JsonFileFeatureStateStore(options, NullLogger<JsonFileFeatureStateStore>.Instance);
        _actions = new JsonFileActionQueue(options, NullLogger<JsonFileActionQueue>.Instance);
        _writes = new JsonLinesWriteLog(options, NullLogger<JsonLinesWriteLog>.Instance);
        _store = new JsonFileOperatorStore(options, NullLogger<JsonFileOperatorStore>.Instance);
    }

    public void Dispose()
    {
        _states.Dispose();
        _actions.Dispose();
        _writes.Dispose();
        _store.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Every_Project_Status_And_Claim_Status_The_Operator_Sends_Is_Accepted_By_The_Control_Planes_Worker_Validation()
    {
        var features = new FeaturesService(
            new FeatureCatalog(), _states, new InMemoryCredentialStore(), new NullFeatureActivator(), []);
        await features.SetWriteGrantAsync(FeatureCatalog.YouTubeId, granted: true, confirmWrites: true, CancellationToken.None);
        var service = new OperatorService(_store, new WriteGateService(new FeatureCatalog(), _states, _actions, _writes), _writes);
        string[] sent = [OperatorPipeline.Researching, OperatorPipeline.Scripting, OperatorPipeline.ScriptReady, OperatorPipeline.Failed];

        var saved = new List<OperatorProject>();
        foreach (var status in sent)
        {
            saved.Add(await service.SaveProjectFromWorkerAsync(
                new OperatorProject(string.Empty, "Why rivers meander", status, string.Empty, default, default, [], null,
                    [new Claim("c1", "Most rivers meander.", "standard", [], OperatorPipeline.Pending)]),
                null,
                CancellationToken.None));
        }

        saved.Select(p => p.Status).ShouldBe(sent);
        saved.SelectMany(p => p.Claims).Select(c => c.Status).ShouldAllBe(s => s == OperatorPipeline.Pending);
    }

    [Fact]
    public async Task Every_Production_Status_And_Scene_Value_The_Operator_Sends_Is_Accepted_By_The_Control_Planes_Worker_Validation()
    {
        var features = new FeaturesService(
            new FeatureCatalog(), _states, new InMemoryCredentialStore(), new NullFeatureActivator(), []);
        await features.SetWriteGrantAsync(FeatureCatalog.YouTubeId, granted: true, confirmWrites: true, CancellationToken.None);
        var service = new OperatorService(_store, new WriteGateService(new FeatureCatalog(), _states, _actions, _writes), _writes);
        Scene[] scenes =
        [
            new("s1", 1, "Rivers bend.", "A river from above", false, OperatorPipeline.Planned, null,
                OperatorPipeline.NoAudio, OperatorPipeline.TextCard, null, null, 0),
            new("s2", 2, "Water cuts the outer bank.", "An eroded bank", false, OperatorPipeline.Ready, null,
                OperatorPipeline.Generated, OperatorPipeline.Generated, "scene-s2.mp3", "scene-s2.png", 2400),
            new("s3", 3, "Floods move the bends.", "A flooded plain", false, OperatorPipeline.Ready, NarrationGenerator.NoSpeech,
                OperatorPipeline.NoAudio, OperatorPipeline.TextCard, null, null, 1600),
            new("s4", 4, "Subscribe.", "A map", false, OperatorPipeline.Failed, NarrationGenerator.Failed(4),
                OperatorPipeline.NoAudio, OperatorPipeline.TextCard, null, null, 0),
            new("s5", 5, "Meanders migrate.", "A river bend", false, OperatorPipeline.Ready, null,
                OperatorPipeline.Generated, OperatorPipeline.TextCard, "scene-s5.wav", null, 1600),
            new("s6", 6, "Oxbows form.", "An oxbow lake", false, OperatorPipeline.Failed, PiperSpeech.UnknownVoice("piper:en_GB-alan-medium"),
                OperatorPipeline.NoAudio, OperatorPipeline.TextCard, null, null, 0),
            new("s7", 7, "Deltas grow.", "A river delta", false, OperatorPipeline.Ready, OpenversePictures.Found,
                OperatorPipeline.NoAudio, OperatorPipeline.Stock, null, "scene-s7.jpg", 0)
            {
                VisualQuery = "river delta aerial",
                VisualCredit = "\"Delta\" by Ana is licensed under CC BY 2.0.",
            },
            new("s8", 8, "Lakes fill.", "A lake", false, OperatorPipeline.Ready, OpenversePictures.Unreachable,
                OperatorPipeline.NoAudio, OperatorPipeline.TextCard, null, null, 0) { VisualQuery = "lake" },
        ];
        string[] sent = [OperatorPipeline.Producing, OperatorPipeline.MediaReady];

        var saved = new List<OperatorProject>();
        foreach (var status in sent)
        {
            saved.Add(await service.SaveProjectFromWorkerAsync(
                new OperatorProject(string.Empty, "Why rivers meander", status, string.Empty, default, default, [], null, []) { Scenes = scenes },
                null,
                CancellationToken.None));
        }

        saved.Select(p => p.Status).ShouldBe(sent);
        saved.ShouldAllBe(p => p.Scenes.SequenceEqual(scenes));
    }

    [Theory]
    [InlineData("OpenAI:shimmer")]
    [InlineData("AzureOpenAI:alloy")]
    [InlineData("piper:en_GB-cori-medium")]
    [InlineData("piper:en_GB-aru-medium#11")]
    public async Task Every_Voice_Id_The_Worker_Offers_Is_A_Narration_Voice_The_Control_Planes_Produce_Keeps(string voice)
    {
        var features = new FeaturesService(
            new FeatureCatalog(), _states, new InMemoryCredentialStore(), new NullFeatureActivator(), []);
        await features.SetWriteGrantAsync(FeatureCatalog.YouTubeId, granted: true, confirmWrites: true, CancellationToken.None);
        var service = new OperatorService(_store, new WriteGateService(new FeatureCatalog(), _states, _actions, _writes), _writes);
        var created = await service.SaveProjectFromWorkerAsync(
            new OperatorProject(string.Empty, "Why rivers meander", OperatorPipeline.ScriptReady, string.Empty, default, default, [], null, []),
            null,
            CancellationToken.None);

        var produced = await service.ProduceProjectAsync(created.Id, voice, string.Empty, string.Empty, CancellationToken.None);

        produced.NarrationVoice.ShouldBe(voice);
        (await service.GetProjectForWorkerAsync(created.Id, CancellationToken.None)).NarrationVoice.ShouldBe(voice);
    }

    [Theory]
    [InlineData(Assembler.Still)]
    [InlineData(Assembler.Gentle)]
    [InlineData(Assembler.Dynamic)]
    public async Task Every_Motion_Preset_The_Assembler_Applies_Is_One_The_Control_Planes_Produce_Keeps(string motion)
    {
        var features = new FeaturesService(
            new FeatureCatalog(), _states, new InMemoryCredentialStore(), new NullFeatureActivator(), []);
        await features.SetWriteGrantAsync(FeatureCatalog.YouTubeId, granted: true, confirmWrites: true, CancellationToken.None);
        var service = new OperatorService(_store, new WriteGateService(new FeatureCatalog(), _states, _actions, _writes), _writes);
        var created = await service.SaveProjectFromWorkerAsync(
            new OperatorProject(string.Empty, "Why rivers meander", OperatorPipeline.ScriptReady, string.Empty, default, default, [], null, []),
            null,
            CancellationToken.None);

        var produced = await service.ProduceProjectAsync(created.Id, string.Empty, string.Empty, motion, CancellationToken.None);

        produced.Motion.ShouldBe(motion);
        (await service.GetProjectForWorkerAsync(created.Id, CancellationToken.None)).Motion.ShouldBe(motion);
    }

    [Fact]
    public async Task A_Scene_Save_From_A_Read_Older_Than_An_Owner_Reorder_Keeps_The_Owners_Order_And_After_A_Regenerate_Is_Refused_In_The_Words_The_Pipeline_Stops_On()
    {
        var features = new FeaturesService(
            new FeatureCatalog(), _states, new InMemoryCredentialStore(), new NullFeatureActivator(), []);
        await features.SetWriteGrantAsync(FeatureCatalog.YouTubeId, granted: true, confirmWrites: true, CancellationToken.None);
        var service = new OperatorService(_store, new WriteGateService(new FeatureCatalog(), _states, _actions, _writes), _writes);
        Scene Planned(string id, int order) => new(id, order, $"Narration {id}.", $"Picture {id}", false, OperatorPipeline.Planned, null,
            OperatorPipeline.NoAudio, OperatorPipeline.TextCard, null, null, 0);
        Scene Produced(Scene scene) => scene with
        {
            Status = OperatorPipeline.Ready,
            AudioSource = OperatorPipeline.Generated,
            VisualSource = OperatorPipeline.Generated,
            AudioFile = $"scene-{scene.Id}.mp3",
            VisualFile = $"scene-{scene.Id}.png",
            DurationMs = 800,
        };
        OperatorProject WithFirstProduced(OperatorProject read) =>
            read with { Scenes = [.. read.Scenes.Select(s => s.Id == "s1" ? Produced(s) : s)] };
        var created = await service.SaveProjectFromWorkerAsync(
            new OperatorProject(string.Empty, "Why rivers meander", OperatorPipeline.Producing, string.Empty, default, default, [], null, [])
            {
                Scenes = [Planned("s1", 1), Planned("s2", 2), Planned("s3", 3)],
            },
            null,
            CancellationToken.None);
        var beforeReorder = await service.GetProjectForWorkerAsync(created.Id, CancellationToken.None);

        await service.ReorderScenesAsync(created.Id, ["s3", "s1", "s2"], CancellationToken.None);
        var accepted = await service.SaveProjectFromWorkerAsync(WithFirstProduced(beforeReorder), null, CancellationToken.None);
        await service.RegenerateSceneAsync(created.Id, "s2", CancellationToken.None);
        var refused = await Should.ThrowAsync<OperatorTamperingException>(
            () => service.SaveProjectFromWorkerAsync(WithFirstProduced(beforeReorder), null, CancellationToken.None));
        var regenerated = await service.GetProjectForWorkerAsync(created.Id, CancellationToken.None);

        accepted.Scenes.OrderBy(s => s.Id).Select(s => (s.Id, s.Order, s.Status)).ShouldBe([
            ("s1", 2, OperatorPipeline.Ready), ("s2", 3, OperatorPipeline.Planned), ("s3", 1, OperatorPipeline.Planned),
        ]);
        refused.Message.ShouldBe(OperatorPipeline.NoLongerProducing);
        regenerated.Status.ShouldBe(OperatorService.ProductionQueued);
        regenerated.Scenes.ShouldBe(accepted.Scenes);
    }

    [Fact]
    public void Every_Scope_The_Operator_Writes_Under_Is_A_YouTube_Catalog_Row_Of_The_Same_Access()
    {
        var rows = new FeatureCatalog().Find(FeatureCatalog.YouTubeId).ShouldNotBeNull().ScopeMatrix.ShouldNotBeNull()
            .ToDictionary(r => r.Scope, r => r.Access);

        new[] { OperatorServiceCollectionExtensions.DataFolderScope, ReachReports.JobScope }
            .ShouldAllBe(scope => rows.ContainsKey(scope) && rows[scope] == ScopeAccess.Write);
        new[] { ActionExecutor.Insert, ActionExecutor.Update, ActionExecutor.Thumbnail, ActionExecutor.Reply, ActionExecutor.Hold }
            .ShouldAllBe(scope => rows.ContainsKey(scope) && rows[scope] == ScopeAccess.IrreversibleWrite);
        ActionExecutor.FeatureId.ShouldBe(FeatureCatalog.YouTubeId);
    }

    public static TheoryData<string[]> TagSets() => new()
    {
        Array.Empty<string>(),
        new[] { "rivers" },
        new[] { "rivers", "river bends", "oxbow lakes", "geography" },
        new[] { "a b c d", " ", "x" },
        Enumerable.Range(0, 30).Select(i => $"tag {i} with spaces").ToArray(),
    };

    [Theory]
    [MemberData(nameof(TagSets))]
    public void The_Operator_And_The_Control_Plane_Count_Tags_The_Same_Way_YouTube_Does(string[] tags)
        => PackageChecks.TagsLength(tags).ShouldBe(PackageRules.TagsLength(tags));

    [Theory]
    [InlineData("Why Rivers Bend", "Intro\n\n0:00 Erosion\n0:20 Floods", "rivers")]
    [InlineData("Amazing Geography", "0:00 Erosion", "rivers")]
    [InlineData("You will not believe this", "No list here.", "rivers")]
    [InlineData("Rivers", "Chapters 0:00 inline only", "why rivers bend")]
    public void The_Operator_And_The_Control_Plane_Check_Discoverability_The_Same_Way(string title, string description, string topic)
    {
        var worker = PackageChecks.Discoverability(title, description, topic, new OperatorMandate(
            "Teach", "Students", ["geography"], "weekly", "en", "calm", [], "views", "1000"));
        var controlPlane = PackageRules.WithMetadataChecks([], title, description, [], topic, ["geography"]).Single(c => c.Name == "discoverability");

        (controlPlane.Name, controlPlane.Result, controlPlane.Detail).ShouldBe((worker.Name, worker.Result, worker.Detail));
    }

    [Fact]
    public void A_First_Title_From_A_120_Character_Variant_Or_A_200_Character_Topic_Is_Metadata_The_Control_Plane_Accepts()
    {
        var script = new OperatorScript("Hook", [], "Subscribe", [string.Join(' ', Enumerable.Repeat("Sourdough", 12)) + " bread"], "How to bake.", ["bread"], []);
        var topic200 = string.Concat(Enumerable.Repeat("sourdough ", 19)) + "starter bread";

        PackageRules.MetadataErrors(PackageChecks.Title(script, "sourdough"), "How to bake.", ["bread"]).ShouldBeEmpty();
        PackageRules.MetadataErrors(PackageChecks.Title(script with { TitleVariants = [] }, topic200), "How to bake.", ["bread"]).ShouldBeEmpty();
    }

    [Fact]
    public async Task Comment_Kinds_And_Statuses_The_Worker_Sends_Are_Accepted()
    {
        var features = new FeaturesService(
            new FeatureCatalog(), _states, new InMemoryCredentialStore(), new NullFeatureActivator(), []);
        await features.SetWriteGrantAsync(FeatureCatalog.YouTubeId, granted: true, confirmWrites: true, CancellationToken.None);
        var gate = new WriteGateService(new FeatureCatalog(), _states, _actions, _writes);
        var service = new OperatorService(_store, gate, _writes);
        var comments = new CommentService(_store, service);
        var bridge = new CommentBridge(comments, service, gate);
        var time = TimeProvider.System;
        var youtube = Substitute.For<IYouTubeClient>();
        youtube.GetMyChannelAsync(Arg.Any<CancellationToken>()).Returns(new YouTubeChannel("UCowner", "Rivers", "@rivers", 10, 100, 1, default, "UUowner"));
        youtube.GetVideosAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns([new YouTubeVideo("v1", "Why rivers bend", "Rivers", default, TimeSpan.FromMinutes(5), 10, 1, 4)]);
        YouTubeCommentThread Thread(string id, int minutes, string text) =>
            new($"t-{id}", id, "v1", $"Viewer {id}", $"UC{id}", text, time.GetUtcNow().AddMinutes(-minutes), 1);
        youtube.ListCommentThreadsAsync("UCowner", null, Arg.Any<CancellationToken>()).Returns(
            new YouTubeCommentThreadPage([Thread("q1", 1, "How long did this take?"), Thread("r1", 2, "Please do deltas next"), Thread("s1", 3, "Buy followers"), Thread("p1", 4, "Lovely")], null),
            new YouTubeCommentThreadPage([], null));
        var chat = new SequenceChat(
            """{"comments":[{"id":"c1","kind":"question","theme":"effort"},{"id":"c2","kind":"request","theme":"deltas","request":"How deltas form"},{"id":"c3","kind":"spam","theme":"followers"},{"id":"c4","kind":"praise","theme":"thanks"}]}""",
            """{"replies":[{"id":"c1","reply":"About a week."},{"id":"c2","reply":"Noted, thank you."}]}""",
            """{"replies":[{"id":"c1","reply":"Thank you!"}]}""");
        var desk = new CommentDesk(bridge, youtube, chat, time, NullLogger<CommentDesk>.Instance);

        (await desk.RunAsync(sync: true, CancellationToken.None)).ShouldBeTrue();
        await comments.DecideCommentAsync("p1", "draft", CancellationToken.None);
        await comments.RequestCommentSyncAsync(CancellationToken.None);
        (await desk.RunAsync(sync: true, CancellationToken.None)).ShouldBeTrue();
        var drafted = await comments.ListCommentsAsync(CancellationToken.None);

        drafted.Comments.Select(c => (c.Id, c.Kind, c.Status)).OrderBy(c => c.Id).ShouldBe([
            ("p1", "praise", "drafted"), ("q1", "question", "drafted"), ("r1", "request", "drafted"), ("s1", "spam", "quarantined")]);
        drafted.Ideas.ShouldBe([new AudienceIdea("idea-r1", "How deltas form", "r1", "proposed", null)]);
        (drafted.SyncRequestedAt, drafted.SyncError).ShouldBe((null, null));
        drafted.Comments.Single(c => c.Id == "p1").DecidedAt.ShouldNotBeNull();

        await comments.ApproveReplyAsync("q1", "About a week of fieldwork.", CancellationToken.None);
        await comments.DecideCommentAsync("s1", "hold", CancellationToken.None);
        var write = Substitute.For<IYouTubeWriteClient>();
        write.ReplyToCommentAsync("q1", "About a week of fieldwork.", Arg.Any<CancellationToken>()).Returns("q1.reply1");
        var executor = new ActionExecutor(bridge, write, Options.Create(new OperatorMediaOptions()), time, NullLogger<ActionExecutor>.Instance,
            Substitute.For<IOriginNotifier>(), desk);
        await executor.RunOnceAsync(CancellationToken.None);
        await executor.RunOnceAsync(CancellationToken.None);
        var executed = await comments.ListCommentsAsync(CancellationToken.None);

        (executed.Comments.Single(c => c.Id == "q1").Status, executed.Comments.Single(c => c.Id == "q1").ReplyId).ShouldBe(("replied", "q1.reply1"));
        executed.Comments.Single(c => c.Id == "s1").Status.ShouldBe("held");
        (await _writes.ListAsync(50, CancellationToken.None)).Where(w => w.Outcome == "succeeded").Select(w => w.Action).Order()
            .ShouldBe(["comments.insert", "comments.setModerationStatus"]);
        await write.Received(1).HoldCommentAsync("s1", Arg.Any<CancellationToken>());

        youtube.ListCommentThreadsAsync("UCowner", null, Arg.Any<CancellationToken>())
            .Returns<Task<YouTubeCommentThreadPage>>(_ => throw new YouTubeApiException("YouTube is currently unavailable. Try again later.", HttpStatusCode.ServiceUnavailable));
        (await desk.RunAsync(sync: true, CancellationToken.None)).ShouldBeFalse();
        var failed = await comments.ListCommentsAsync(CancellationToken.None);

        (failed.SyncError, failed.SyncedAt, failed.Comments.Count).ShouldBe(("YouTube is currently unavailable. Try again later.", executed.SyncedAt, 4));
    }

    private sealed class SequenceChat(params string[] replies) : IChatCompletionService
    {
        private int _next;

        public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

        public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
            ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ChatMessageContent>>([new ChatMessageContent(AuthorRole.Assistant, replies[Math.Min(_next++, replies.Length - 1)])]);

        public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
            ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// The worker's comment calls answered by the real comment service, with the times carried as the host's mapping
    /// carries them: written in round-trip form and read back as sent, an empty fetch time read as unset.
    /// </summary>
    private sealed class CommentBridge(CommentService comments, OperatorService service, WriteGateService gate) : IOperatorControlPlane
    {
        public Task<OperatorMandate> GetMandateAsync(CancellationToken ct)
            => Task.FromResult(new OperatorMandate("Teach geography", "Students", [], "weekly", "English", "calm", ["diets"], "views", "1000"));

        public Task<OperatorComments> GetCommentsAsync(CancellationToken ct)
            => CallAsync(async () => ToWorker(await comments.GetCommentsForWorkerAsync(ct)));

        public Task<OperatorComments> SaveCommentsAsync(OperatorComments sent, CancellationToken ct)
            => CallAsync(async () => ToWorker(await comments.SaveCommentsFromWorkerAsync(ToDomain(sent), ct)));

        public async Task<OperatorAction?> NextApprovedActionAsync(CancellationToken ct)
            => await service.NextApprovedActionAsync(ct) is { } a ? new OperatorAction(a.Id, a.FeatureId, a.Action, a.Target, a.Summary) : null;

        public Task RecordActionAsync(OperatorAction action, string outcome, CancellationToken ct)
            => gate.RecordAsync(action.FeatureId, action.Action, action.Target, outcome, action.Id, ct);

        public Task<OperatorProjectState> SaveProjectAsync(OperatorProjectState project, string? originReference, CancellationToken ct) => throw new NotSupportedException();

        public Task<QueuedProject?> NextQueuedAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<OperatorProjectState> GetProjectAsync(string id, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<OperatorProjectState>> ListRecentAsync(int limit, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetMediaDirectoryAsync(string projectId, CancellationToken ct) => throw new NotSupportedException();

        public Task ReportReadinessAsync(OperatorReadiness readiness, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetVoiceSamplesDirectoryAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<OperatorProjectState> EditSceneTextAsync(string projectId, string sceneId, string text, CancellationToken ct) => throw new NotSupportedException();

        public Task<OperatorProjectState> RegenerateNarrationAsync(string projectId, string sceneId, CancellationToken ct) => throw new NotSupportedException();

        public Task<OperatorProjectState> RegenerateSceneAsync(string projectId, string sceneId, CancellationToken ct) => throw new NotSupportedException();

        public Task<OperatorProjectState> SetSceneLockedAsync(string projectId, string sceneId, bool locked, CancellationToken ct) => throw new NotSupportedException();

        public Task<OperatorProjectState> ReorderScenesAsync(string projectId, IReadOnlyList<string> sceneIds, CancellationToken ct) => throw new NotSupportedException();

        public Task<OperatorProjectState> ProduceProjectAsync(string projectId, string narrationVoice, string direction, string motion, CancellationToken ct) => throw new NotSupportedException();

        public Task<OperatorProjectState> DelegateProjectAsync(string projectId, string narrationVoice, string direction, string motion, CancellationToken ct) => throw new NotSupportedException();

        private static async Task<T> CallAsync<T>(Func<Task<T>> call)
        {
            try
            {
                return await call();
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                throw new InvalidOperationException(ex.Message, ex);
            }
        }

        private static string Iso(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);

        private static DateTimeOffset Parse(string at) => DateTimeOffset.Parse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

        private static OperatorComments ToWorker(CommentsState state) => new(
            [.. state.Comments.Select(c => new OperatorComment(
                c.Id, c.ThreadId, c.VideoId, c.VideoTitle, c.Author, c.Text, Iso(c.PublishedAt), c.LikeCount, c.Kind, c.Theme, c.Status, c.Draft,
                c.ApprovedText, c.ReplyId, c.Error, c.DecidedAt is { } d ? Iso(d) : null, Iso(c.FetchedAt)))],
            [.. state.Ideas.Select(i => new OperatorIdea(i.Id, i.Text, i.CommentId, i.Status, i.ProjectId))],
            state.SyncedAt is { } s ? Iso(s) : null,
            state.SyncRequestedAt is { } r ? Iso(r) : null,
            state.SyncError);

        private static CommentsState ToDomain(OperatorComments sent) => new(
            [.. sent.Comments.Select(c => new AudienceComment(
                c.Id, c.ThreadId, c.VideoId, c.VideoTitle, c.Author, c.Text, Parse(c.PublishedAt), c.LikeCount, c.Kind, c.Theme, c.Status, c.Draft,
                c.ApprovedText, c.ReplyId, c.Error, c.DecidedAt is { } d ? Parse(d) : null, c.FetchedAt.Length == 0 ? default : Parse(c.FetchedAt)))],
            [.. sent.Ideas.Select(i => new AudienceIdea(i.Id, i.Text, i.CommentId, i.Status, i.ProjectId))],
            sent.SyncedAt is { } s ? Parse(s) : null,
            sent.SyncRequestedAt is { } r ? Parse(r) : null,
            sent.SyncError);
    }

    [Fact]
    public async Task The_Chat_Edits_The_Plugin_Sends_Are_Ones_The_Control_Plane_Accepts()
    {
        var features = new FeaturesService(
            new FeatureCatalog(), _states, new InMemoryCredentialStore(), new NullFeatureActivator(), []);
        await features.SetWriteGrantAsync(FeatureCatalog.YouTubeId, granted: true, confirmWrites: true, CancellationToken.None);
        var service = new OperatorService(_store, new WriteGateService(new FeatureCatalog(), _states, _actions, _writes), _writes);
        Scene Ready(string id, int order) => new(id, order, $"Narration {id}.", $"Picture {id}", false, OperatorPipeline.Ready, null,
            OperatorPipeline.Generated, OperatorPipeline.Generated, $"scene-{id}.mp3", $"scene-{id}.png", 800);
        async Task<string> MediaReadyAsync()
        {
            var producing = await service.SaveProjectFromWorkerAsync(
                new OperatorProject(string.Empty, "Why rivers meander", OperatorPipeline.Producing, string.Empty, default, default, [], null, [])
                {
                    Scenes = [Ready("s1", 1), Ready("s2", 2), Ready("s3", 3)],
                },
                null,
                CancellationToken.None);
            var read = await service.GetProjectForWorkerAsync(producing.Id, CancellationToken.None);
            return (await service.SaveProjectFromWorkerAsync(read with { Status = OperatorPipeline.MediaReady }, null, CancellationToken.None)).Id;
        }

        var produced = await MediaReadyAsync();
        var delegated = await MediaReadyAsync();
        var plugin = new YouTubeOperatorPlugin(
            new ChatBridge(service),
            System.Threading.Channels.Channel.CreateUnbounded<QueuedProject>(),
            _ => Task.FromResult(new OperatorReadiness(true, true, "OpenAI", true, true, [])));
        Func<Task<string>>[] calls =
        [
            () => plugin.LockSceneAsync(produced, "s1", true),
            () => plugin.LockSceneAsync(produced, "s1", false),
            () => plugin.ReorderScenesAsync(produced, "s2,s1,s3"),
            () => plugin.ProduceProjectAsync(produced, narration_voice: "piper:en_GB-cori-medium", motion: "still"),
            () => plugin.EditSceneTextAsync(produced, "s2", "Rivers bend where the bank is soft."),
            () => plugin.RegenerateNarrationAsync(produced, "s2"),
            () => plugin.RegenerateSceneAsync(produced, "s3"),
            () => plugin.DelegateProjectAsync(delegated),
        ];
        async IAsyncEnumerable<string> RunAll()
        {
            await plugin.ProjectStateAsync(produced);
            await plugin.ProjectStateAsync(delegated);
            foreach (var call in calls)
            {
                string reply;
                try
                {
                    reply = await call();
                }
                catch (InvalidOperationException ex)
                {
                    reply = $"refused: {ex.Message}";
                }

                yield return reply + "\n";
            }
        }

        var router = Substitute.For<Scout.Conversation.Abstractions.IConversationRouter>();
        router.Route(Arg.Any<Scout.Conversation.Abstractions.ChatMessage>()).Returns(_ => new Scout.Conversation.Abstractions.ChatResponse(RunAll()));
        var sink = Substitute.For<Scout.Conversation.Abstractions.IResponseSink>();
        sink.Capabilities.Returns(new Scout.Conversation.Abstractions.RenderCapabilities { FineGrainedEdit = true, MaxBodyLength = 8192 });
        string? final = null;
        sink.CompleteAsync(Arg.Do<string>(text => final = text), Arg.Any<IReadOnlyList<Scout.Conversation.Abstractions.Citation>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        await new Scout.Conversation.ConversationConductor(router, new Scout.Conversation.Streaming.StreamPump(new Scout.Conversation.Streaming.StreamPumpOptions()))
            .HandleAsync(
                new Scout.Conversation.Abstractions.ChatMessage("local-owner", "local", "change the video") { IsLocalOwner = true },
                sink,
                CancellationToken.None);

        final.ShouldNotBeNull().Trim().Split('\n').ShouldBe([
            "Locked scene s1 on Why rivers meander; the project is now media_ready.",
            "Unlocked scene s1 on Why rivers meander; the project is now media_ready.",
            "Put the scenes in the order s2, s1, s3 on Why rivers meander; the project is now media_ready.",
            "Queued production on Why rivers meander; the project is now production_queued.",
            "Edited the text of scene s2 on Why rivers meander; the project is now production_queued.",
            "Asked for the narration of scene s2 again on Why rivers meander; the project is now production_queued.",
            "Asked for scene s3 again on Why rivers meander; the project is now production_queued.",
            "Delegated it: Scout produces and assembles it and stops at ready to approve on Why rivers meander; the project is now assembly_queued.",
        ]);
        (await _writes.ListAsync(50, CancellationToken.None)).Select(w => (w.Target, w.Outcome)).Reverse().ShouldBe([
            (produced, "saved producing"),
            (produced, "saved media_ready"),
            (delegated, "saved producing"),
            (delegated, "saved media_ready"),
            (produced, "locked scene s1 via chat"),
            (produced, "unlocked scene s1 via chat"),
            (produced, "reordered the scenes via chat"),
            (produced, "queued production via chat"),
            (produced, "edited the text of scene s2 via chat"),
            (produced, "asked for scene s3 again via chat"),
            (delegated, "delegated via chat"),
        ]);
        var stored = await service.GetProjectForWorkerAsync(produced, CancellationToken.None);
        (stored.NarrationVoice, stored.Motion, stored.Scenes.Single(s => s.Id == "s2").Narration).ShouldBe(
            ("piper:en_GB-cori-medium", "still", "Rivers bend where the bank is soft."));
    }

    [Fact]
    public async Task A_Late_Transcribing_Save_Is_Refused_By_The_Control_Plane_With_The_Sentence_The_Worker_Stops_Quietly_On()
    {
        var features = new FeaturesService(
            new FeatureCatalog(), _states, new InMemoryCredentialStore(), new NullFeatureActivator(), []);
        await features.SetWriteGrantAsync(FeatureCatalog.YouTubeId, granted: true, confirmWrites: true, CancellationToken.None);
        var service = new OperatorService(_store, new WriteGateService(new FeatureCatalog(), _states, _actions, _writes), _writes);
        var started = await service.StartProjectAsync("A walk by the river", OperatorPipeline.OwnVideo, string.Empty, CancellationToken.None);
        await File.WriteAllBytesAsync(
            Path.Combine(await service.GetMediaDirectoryAsync(started.Id, CancellationToken.None), "owner-recording-0123456789ab.mp4"), [0, 0, 0, 24]);
        await service.AttachSceneMediaAsync(started.Id, string.Empty, OperatorPipeline.Recording, "owner-recording-0123456789ab.mp4", CancellationToken.None);
        var taken = (await service.NextQueuedProjectAsync(CancellationToken.None)).ShouldNotBeNull();
        await service.SaveProjectFromWorkerAsync(taken with { Status = OperatorPipeline.Failed, Error = "Scout heard no speech in the recording." }, null, CancellationToken.None);

        var refused = await Should.ThrowAsync<OperatorTamperingException>(
            () => service.SaveProjectFromWorkerAsync(taken with { RecordingMs = 47_000 }, null, CancellationToken.None));

        refused.Message.ShouldBe(OperatorPipeline.NoLongerTranscribing);
        (await service.GetProjectForWorkerAsync(started.Id, CancellationToken.None)).Status.ShouldBe(OperatorPipeline.Failed);
    }

    [Fact]
    public async Task The_Transcribing_Saves_And_The_Recording_Scenes_With_Clips_The_Worker_Sends_Are_Accepted_By_The_Control_Plane()
    {
        var features = new FeaturesService(
            new FeatureCatalog(), _states, new InMemoryCredentialStore(), new NullFeatureActivator(), []);
        await features.SetWriteGrantAsync(FeatureCatalog.YouTubeId, granted: true, confirmWrites: true, CancellationToken.None);
        var service = new OperatorService(_store, new WriteGateService(new FeatureCatalog(), _states, _actions, _writes), _writes);
        var started = await service.StartProjectAsync("A walk by the river", OperatorPipeline.OwnVideo, string.Empty, CancellationToken.None);
        await File.WriteAllBytesAsync(
            Path.Combine(await service.GetMediaDirectoryAsync(started.Id, CancellationToken.None), "owner-recording-0123456789ab.mp4"), [0, 0, 0, 24]);
        await service.AttachSceneMediaAsync(started.Id, string.Empty, OperatorPipeline.Recording, "owner-recording-0123456789ab.mp4", CancellationToken.None);
        var taken = (await service.NextQueuedProjectAsync(CancellationToken.None)).ShouldNotBeNull();
        OperatorScriptSection[] heard =
        [
            new(string.Empty, "Here the river is wide and slow.") { StartMs = 0, EndMs = 12_000 },
            new(string.Empty, "It bends where the bank is soft.") { StartMs = 12_000, EndMs = 24_000 },
            new(string.Empty, "And there the water cuts the bank away.") { StartMs = 24_800, EndMs = 46_000 },
        ];
        static Script Sent(OperatorScript script) => new(
            script.Hook,
            [.. script.Sections.Select(s => new ScriptSection(s.Heading, s.Narration) { StartMs = s.StartMs, EndMs = s.EndMs })],
            script.CallToAction,
            script.TitleVariants,
            script.Description,
            script.Tags,
            [.. script.Chapters.Select(c => new Chapter(c.At, c.Title))]);
        OperatorScript Transcript(int count) => new(string.Empty, [.. heard.Take(count)], string.Empty, [], string.Empty, [], []);

        var saves = new List<OperatorProject>();
        foreach (var count in new[] { 0, 2, 3 })
        {
            saves.Add(await service.SaveProjectFromWorkerAsync(
                taken with { Status = OperatorPipeline.Transcribing, RecordingMs = 47_000, Script = Sent(Transcript(count)) }, null, CancellationToken.None));
        }

        saves.Add(await service.SaveProjectFromWorkerAsync(
            taken with { Status = OperatorPipeline.ScriptReady, RecordingMs = 47_000, Script = Sent(Transcript(3)) }, null, CancellationToken.None));
        await service.ProduceProjectAsync(started.Id, string.Empty, string.Empty, string.Empty, CancellationToken.None);
        var producing = (await service.NextQueuedProjectAsync(CancellationToken.None)).ShouldNotBeNull();
        var plan = await new ScenePlanner(new SequenceChat(
                """{"title_variants":["A river bends"],"description":"Why rivers bend.","tags":["rivers"],"chapters":[{"scene":1,"title":"The bend"},{"scene":2,"title":"The cut"}]}"""))
            .PlanRecordingAsync(Transcript(3), producing.RecordingMs, new OperatorMandate("Teach", "Students", ["geography"], "weekly", "en", "calm", [], "views", "1000"), string.Empty, CancellationToken.None);
        Scene[] scenes = [.. plan.Scenes.Select(s => new Scene(
            s.Id, s.Order, s.Narration, s.VisualPrompt, s.Locked, s.Status, s.Error, s.AudioSource, s.VisualSource, s.AudioFile, s.VisualFile, s.DurationMs)
        {
            ClipStartMs = s.ClipStartMs,
            ClipEndMs = s.ClipEndMs,
        })];
        foreach (var status in new[] { OperatorPipeline.Producing, OperatorPipeline.MediaReady })
        {
            saves.Add(await service.SaveProjectFromWorkerAsync(
                producing with { Status = status, Scenes = scenes, Script = Sent(plan.Script) }, null, CancellationToken.None));
        }

        saves.Select(p => (p.Status, p.RecordingMs, p.Script?.Sections.Count)).ShouldBe([
            (OperatorPipeline.Transcribing, 47_000, 0), (OperatorPipeline.Transcribing, 47_000, 2), (OperatorPipeline.Transcribing, 47_000, 3),
            (OperatorPipeline.ScriptReady, 47_000, 3), (OperatorPipeline.Producing, 47_000, 2), (OperatorPipeline.MediaReady, 47_000, 2),
        ]);
        saves[3].Script.ShouldNotBeNull().Sections.Select(s => (s.StartMs, s.EndMs)).ShouldBe([(0, 12_000), (12_000, 24_000), (24_800, 46_000)]);
        var stored = await service.GetProjectForWorkerAsync(started.Id, CancellationToken.None);
        stored.Scenes.Select(s => (s.Id, s.AudioSource, s.VisualSource, s.ClipStartMs, s.ClipEndMs, s.DurationMs)).ShouldBe([
            ("s1", OperatorPipeline.Recording, OperatorPipeline.Recording, 0, 24_800, 24_800),
            ("s2", OperatorPipeline.Recording, OperatorPipeline.Recording, 24_800, 47_000, 22_200),
        ]);
        stored.Script.ShouldNotBeNull().Sections.Select(s => (s.Heading, s.StartMs, s.EndMs)).ShouldBe([("The bend", 0, 24_800), ("The cut", 24_800, 47_000)]);
    }

    [Fact]
    public async Task An_Animated_Character_Save_Fills_The_Presenter_Picture_Once_And_Carries_Lip_Sync_In_The_Readiness()
    {
        var features = new FeaturesService(
            new FeatureCatalog(), _states, new InMemoryCredentialStore(), new NullFeatureActivator(), []);
        await features.SetWriteGrantAsync(FeatureCatalog.YouTubeId, granted: true, confirmWrites: true, CancellationToken.None);
        var writeGate = new WriteGateService(new FeatureCatalog(), _states, _actions, _writes);
        var service = new OperatorService(_store, writeGate, _writes);
        await using var server = await GrpcWriteGateTests.WorkerGateServer.StartAsync(
            new Scout.Host.ControlPlane.Services.WorkerGateGrpcService(writeGate, service, new CommentService(_store, service)));
        using var gate = new GrpcWriteGate(server.SocketPath, "worker-session-token");
        var worker = new GrpcOperatorControlPlane(gate);
        OperatorScene Spoken(string id, int order) => new(id, order, $"Narration {id}.", string.Empty, false, OperatorPipeline.Ready, null,
            OperatorPipeline.Generated, OperatorPipeline.Presenter, $"scene-{id}.mp3", null, 1600);
        var started = await service.StartProjectAsync("Why rivers meander", OperatorPipeline.AnimatedCharacter, string.Empty, "A friendly fox.", CancellationToken.None);
        var researching = (await worker.NextQueuedAsync(CancellationToken.None)).ShouldNotBeNull().Project;
        await worker.SaveProjectAsync(
            researching with
            {
                Status = OperatorPipeline.ScriptReady,
                Script = new OperatorScript("Hook", [new OperatorScriptSection("Bends", "Rivers bend.")], "Subscribe", ["A", "B", "C"], string.Empty, [], []),
            },
            null,
            CancellationToken.None);
        await service.ProduceProjectAsync(started.Id, string.Empty, string.Empty, string.Empty, CancellationToken.None);
        var producing = (await worker.NextQueuedAsync(CancellationToken.None)).ShouldNotBeNull().Project;

        var scenes = await worker.SaveProjectAsync(producing with { Scenes = [Spoken("s1", 1), Spoken("s2", 2)] }, null, CancellationToken.None);
        await File.WriteAllBytesAsync(Path.Combine(await service.GetMediaDirectoryAsync(started.Id, CancellationToken.None), "presenter-0a1b2c3d4e5f.png"), [1]);
        var filled = await worker.SaveProjectAsync(scenes with { PresenterFile = "presenter-0a1b2c3d4e5f.png" }, null, CancellationToken.None);
        var later = await worker.SaveProjectAsync(
            filled with { Status = OperatorPipeline.MediaReady, PresenterFile = "presenter-ffffffffffff.png" }, null, CancellationToken.None);
        await worker.ReportReadinessAsync(new OperatorReadiness(true, true, "OpenAI", true, true, []) { LipSyncAvailable = true }, CancellationToken.None);
        var canMoveTheMouth = (await service.GetReadinessAsync(CancellationToken.None)).LipSyncAvailable;
        await worker.ReportReadinessAsync(new OperatorReadiness(true, true, "OpenAI", true, true, []), CancellationToken.None);
        var cannotMoveTheMouth = (await service.GetReadinessAsync(CancellationToken.None)).LipSyncAvailable;

        (producing.Format, producing.Presenter, producing.PresenterFile).ShouldBe((OperatorPipeline.AnimatedCharacter, "A friendly fox.", (string?)null));
        scenes.Scenes.Select(s => (s.Id, s.VisualSource, s.VisualFile)).ShouldBe([
            ("s1", OperatorPipeline.Presenter, (string?)null), ("s2", OperatorPipeline.Presenter, (string?)null),
        ]);
        scenes.PresenterFile.ShouldBeNull();
        filled.PresenterFile.ShouldBe("presenter-0a1b2c3d4e5f.png");
        (later.Status, later.PresenterFile).ShouldBe((OperatorPipeline.MediaReady, (string?)"presenter-0a1b2c3d4e5f.png"));
        var stored = await service.GetProjectForWorkerAsync(started.Id, CancellationToken.None);
        (stored.Status, stored.PresenterFile).ShouldBe((OperatorPipeline.MediaReady, (string?)"presenter-0a1b2c3d4e5f.png"));
        stored.Scenes.Select(s => s.VisualSource).ShouldBe([OperatorPipeline.Presenter, OperatorPipeline.Presenter]);
        (canMoveTheMouth, cannotMoveTheMouth).ShouldBe((true, false));
    }

    /// <summary>The plugin's chat calls answered by the real operator service's chat methods.</summary>
    private sealed class ChatBridge(OperatorService service) : IOperatorControlPlane
    {
        private static OperatorProjectState ToWorker(OperatorProject p) => new(p.Id, p.Topic, p.Status, p.Origin, string.Empty, string.Empty, [], null, []);

        public async Task<OperatorProjectState> EditSceneTextAsync(string projectId, string sceneId, string text, CancellationToken ct)
            => ToWorker(await service.EditSceneTextFromChatAsync(projectId, sceneId, text, ct));

        public async Task<OperatorProjectState> RegenerateNarrationAsync(string projectId, string sceneId, CancellationToken ct)
            => ToWorker(await service.RegenerateNarrationFromChatAsync(projectId, sceneId, ct));

        public async Task<OperatorProjectState> RegenerateSceneAsync(string projectId, string sceneId, CancellationToken ct)
            => ToWorker(await service.RegenerateSceneFromChatAsync(projectId, sceneId, ct));

        public async Task<OperatorProjectState> SetSceneLockedAsync(string projectId, string sceneId, bool locked, CancellationToken ct)
            => ToWorker(await service.SetSceneLockedFromChatAsync(projectId, sceneId, locked, ct));

        public async Task<OperatorProjectState> ReorderScenesAsync(string projectId, IReadOnlyList<string> sceneIds, CancellationToken ct)
            => ToWorker(await service.ReorderScenesFromChatAsync(projectId, sceneIds, ct));

        public async Task<OperatorProjectState> ProduceProjectAsync(string projectId, string narrationVoice, string direction, string motion, CancellationToken ct)
            => ToWorker(await service.ProduceProjectFromChatAsync(projectId, narrationVoice, direction, motion, ct));

        public async Task<OperatorProjectState> DelegateProjectAsync(string projectId, string narrationVoice, string direction, string motion, CancellationToken ct)
            => ToWorker(await service.DelegateProjectFromChatAsync(projectId, narrationVoice, direction, motion, ct));

        public Task<OperatorMandate> GetMandateAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<OperatorProjectState> SaveProjectAsync(OperatorProjectState project, string? originReference, CancellationToken ct) => throw new NotSupportedException();

        public Task<QueuedProject?> NextQueuedAsync(CancellationToken ct) => throw new NotSupportedException();

        public async Task<OperatorProjectState> GetProjectAsync(string id, CancellationToken ct)
            => ToWorker(await service.GetProjectForWorkerAsync(id, ct));

        public Task<IReadOnlyList<OperatorProjectState>> ListRecentAsync(int limit, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetMediaDirectoryAsync(string projectId, CancellationToken ct) => throw new NotSupportedException();

        public Task ReportReadinessAsync(OperatorReadiness readiness, CancellationToken ct) => throw new NotSupportedException();

        public Task<OperatorAction?> NextApprovedActionAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task RecordActionAsync(OperatorAction action, string outcome, CancellationToken ct) => throw new NotSupportedException();

        public Task<OperatorComments> GetCommentsAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<OperatorComments> SaveCommentsAsync(OperatorComments comments, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetVoiceSamplesDirectoryAsync(CancellationToken ct) => throw new NotSupportedException();
    }
}
