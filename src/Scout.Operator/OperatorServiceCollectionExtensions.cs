using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.SemanticKernel;
using Scout.Llm;

namespace Scout.Operator;

/// <summary>DI composition for the YouTube channel operator.</summary>
public static class OperatorServiceCollectionExtensions
{
    /// <summary>The catalog scope row of the operator's writes into Scout's data folder.</summary>
    public const string DataFolderScope = "Scout's data folder";

    /// <summary>
    /// Register the operator: the research, script, scene, narration, visual, assembly and pipeline stages, the Openverse
    /// stock pictures on the named HTTP client <c>openverse</c>, the background
    /// worker, the readiness reporter, the samples of the bundled voices, the comment desk with the answer to the owner's comment sync and draft requests, which
    /// a key-only setup answers too, with the sign-in sentence as the sync's error, and the <c>YouTubeOperator</c> kernel plugin whose <c>youtube_make_video</c> and owner-only scene, produce and delegate functions are declared writes of the
    /// <c>youtube</c> feature into Scout's data folder. Needs an <see cref="IOperatorControlPlane"/>, a YouTube
    /// client and an origin notifier registered by the caller, and the kernel and media capabilities of
    /// <c>AddScoutLlm</c>. The ffmpeg, font, piper and voices paths come from <see cref="OperatorMediaOptions"/>, which the
    /// caller binds.
    /// </summary>
    public static IServiceCollection AddYouTubeOperator(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SearchBudget>();
        services.AddSingleton<TopicResearcher>();
        services.AddSingleton<ScriptWriter>();
        services.AddSingleton<ScenePlanner>();
        services.AddSingleton<NarrationGenerator>();
        services.AddHttpClient(OpenversePictures.ClientName, client =>
        {
            client.Timeout = OpenversePictures.Timeout;
            client.DefaultRequestHeaders.UserAgent.ParseAdd(OpenversePictures.UserAgent);
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddSingleton<OpenversePictures>();
        services.AddSingleton<VisualGenerator>();
        services.AddSingleton<OperatorPipeline>();
        services.AddSingleton(Channel.CreateUnbounded<QueuedProject>());
        services.AddHostedService<OperatorWorker>();
        services.AddSingleton<ReadinessReporter>();
        services.AddHostedService(sp => sp.GetRequiredService<ReadinessReporter>());
        services.AddHostedService<VoiceSamples>();
        services.AddSingleton<CommentDesk>();
        services.AddHostedService<CommentRequests>();
        services.TryAddSingleton<ScheduleNotes>();
        services.AddSingleton(sp => KernelPluginFactory.CreateFromObject(
            new YouTubeOperatorPlugin(
                sp.GetRequiredService<IOperatorControlPlane>(),
                sp.GetRequiredService<Channel<QueuedProject>>(),
                ct => sp.GetRequiredService<ReadinessReporter>().CurrentAsync(ct)),
            YouTubeOperatorPlugin.PluginName));
        services.AddWriteFunction(
            YouTubeOperatorPlugin.PluginName,
            YouTubeOperatorPlugin.MakeVideo,
            "youtube",
            DataFolderScope,
            summaryArguments: ["topic", "format"]);
        foreach (var (function, shown) in ChatWrites)
        {
            services.AddWriteFunction(YouTubeOperatorPlugin.PluginName, function, "youtube", DataFolderScope, summaryArguments: shown);
        }

        services.TryAddSingleton<IProcessRunner, ProcessRunner>();
        services.AddOptions<OperatorMediaOptions>();
        services.AddSingleton<FfmpegToolset>();
        services.AddSingleton<PiperSpeech>();
        services.AddSingleton<MediaProbe>();
        services.AddSingleton<Assembler>();
        services.AddSingleton<ShortsCutter>();
        services.AddSingleton<ThumbnailMaker>();
        services.AddSingleton<AssemblyStage>();
        services.AddSingleton<TranscriptionStage>();
        return services;
    }

    /// <summary>
    /// Register what publishes and learns from the owner's videos: the executor of the actions the owner approved, its comment
    /// replies and holds included, and the scheduler of the analytics, experiment, proposal, published-check and comments jobs.
    /// Needs <see cref="AddYouTubeOperator"/>, an
    /// <see cref="Scout.Llm.Abstractions.IWriteGate"/>, and the write, Analytics and Reporting clients the YouTube plugin
    /// registers only with the owner's sign-in.
    /// </summary>
    public static IServiceCollection AddYouTubePublishing(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHostedService<ActionExecutor>();
        services.AddSingleton<PublishedCheck>();
        services.AddSingleton<ReachReports>();
        services.AddSingleton<ExperimentRunner>();
        services.AddSingleton<LearningLoop>();
        services.AddSingleton(sp => ScheduledJob.Daily("generation", 6, true, sp.GetRequiredService<LearningLoop>().ProposeNextTopicAsync));
        services.AddSingleton(sp => ScheduledJob.Weekly("strategy review", DayOfWeek.Sunday, 8, true, sp.GetRequiredService<LearningLoop>().ReviewWeekAsync));
        services.AddSingleton(sp => ScheduledJob.Daily("analytics", 9, true, sp.GetRequiredService<LearningLoop>().SnapshotAsync));
        services.AddSingleton(sp => ScheduledJob.Daily("optimisation", 22, true, sp.GetRequiredService<LearningLoop>().ProposeAsync));
        services.AddSingleton(sp => ScheduledJob.Every("experiments", TimeSpan.FromHours(4), true, sp.GetRequiredService<ExperimentRunner>().RunAsync));
        services.AddSingleton(sp => ScheduledJob.Every("published check", PublishedCheck.Every, true, sp.GetRequiredService<PublishedCheck>().RunAsync));
        services.AddSingleton(sp => ScheduledJob.Every("comments", TimeSpan.FromHours(4), true, ct => sp.GetRequiredService<CommentDesk>().RunAsync(sync: true, ct)));
        services.AddHostedService<OperatorScheduler>();
        return services;
    }

    private static readonly (string Function, string[] Shown)[] ChatWrites =
    [
        (YouTubeOperatorPlugin.EditSceneText, ["project_id", "scene_id"]),
        (YouTubeOperatorPlugin.RegenerateNarration, ["project_id", "scene_id"]),
        (YouTubeOperatorPlugin.RegenerateScene, ["project_id", "scene_id"]),
        (YouTubeOperatorPlugin.LockScene, ["project_id", "scene_id"]),
        (YouTubeOperatorPlugin.ReorderScenes, ["project_id", "scene_ids"]),
        (YouTubeOperatorPlugin.ProduceProject, ["project_id"]),
        (YouTubeOperatorPlugin.DelegateProject, ["project_id"]),
    ];
}
