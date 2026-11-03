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
    /// Register the operator: the research, script, scene, narration, visual, assembly and pipeline stages, the background
    /// worker, the readiness reporter, and the <c>YouTubeOperator</c> kernel plugin whose <c>youtube_make_video</c> is declared a write of the
    /// <c>youtube</c> feature into Scout's data folder. Needs an <see cref="IOperatorControlPlane"/>, a YouTube
    /// client and an origin notifier registered by the caller, and the kernel and media capabilities of
    /// <c>AddScoutLlm</c>. The ffmpeg and font paths come from <see cref="OperatorMediaOptions"/>, which the
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
        services.AddSingleton<VisualGenerator>();
        services.AddSingleton<OperatorPipeline>();
        services.AddSingleton(Channel.CreateUnbounded<QueuedProject>());
        services.AddHostedService<OperatorWorker>();
        services.AddHostedService<ReadinessReporter>();
        services.AddSingleton(sp => KernelPluginFactory.CreateFromObject(
            new YouTubeOperatorPlugin(
                sp.GetRequiredService<IOperatorControlPlane>(),
                sp.GetRequiredService<Channel<QueuedProject>>()),
            YouTubeOperatorPlugin.PluginName));
        services.AddWriteFunction(
            YouTubeOperatorPlugin.PluginName,
            YouTubeOperatorPlugin.MakeVideo,
            "youtube",
            DataFolderScope,
            summaryArguments: ["topic"]);
        services.TryAddSingleton<IProcessRunner, ProcessRunner>();
        services.AddOptions<OperatorMediaOptions>();
        services.AddSingleton<FfmpegToolset>();
        services.AddSingleton<MediaProbe>();
        services.AddSingleton<Assembler>();
        services.AddSingleton<ShortsCutter>();
        services.AddSingleton<ThumbnailMaker>();
        services.AddSingleton<AssemblyStage>();
        return services;
    }
}
