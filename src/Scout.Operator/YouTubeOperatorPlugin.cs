using System.ComponentModel;
using System.Threading.Channels;
using Microsoft.SemanticKernel;
using Scout.Conversation.Abstractions;

namespace Scout.Operator;

/// <summary>
/// The channel operator's functions for the model: start a video project, a write into Scout's own data folder
/// that the write gate checks against the owner's YouTube grant, and read the status of the latest projects.
/// </summary>
internal sealed class YouTubeOperatorPlugin(
    IOperatorControlPlane controlPlane,
    Channel<QueuedProject> started)
{
    internal const string PluginName = "YouTubeOperator";
    internal const string MakeVideo = "youtube_make_video";
    internal const string NoProjects = "No video project exists yet.";

    internal const int StatusCount = 5;

    internal static string Started(string topic, bool postsBack) => postsBack
        ? $"Started a video project on {topic}; I will post here when the script is ready."
        : $"Started a video project on {topic}; follow it in Scout Desktop, Production.";

    internal static string NotStarted(string topic, string reason) =>
        $"Could not start a video project on {topic}: {reason}";

    internal static string NoStatus(string reason) => $"Could not read the video projects: {reason}";

    /// <summary>Start a video project on a topic.</summary>
    /// <param name="topic">What the video is about.</param>
    /// <param name="ct">Cancels the request to the control plane.</param>
    /// <returns>That the project started, in a sentence for the user.</returns>
    /// <exception cref="InvalidOperationException">
    /// The project did not start; the message says why in a sentence for the user, so the write gate records
    /// the write as failed and the model reads the reason.
    /// </exception>
    [KernelFunction(MakeVideo)]
    [Description("Start a YouTube video project on a topic: Scout researches the topic and writes a script the owner reviews in Scout Desktop. Uploads nothing. Needs the owner's write permission for YouTube.")]
    public async Task<string> MakeVideoAsync(
        [Description("What the video is about, 3 to 200 characters.")] string topic,
        CancellationToken ct = default)
    {
        var origin = ChatOrigin.Current;
        var trimmed = (topic ?? string.Empty).Trim();
        OperatorMandate mandate;
        try
        {
            mandate = await controlPlane.GetMandateAsync(ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(NotStarted(trimmed, ex.Message), ex);
        }

        if (TopicResearcher.BlockedTopic(mandate, trimmed) is { } blocked)
        {
            throw new InvalidOperationException(NotStarted(trimmed, TopicResearcher.Blocked(blocked)));
        }

        OperatorProjectState project;
        try
        {
            project = await controlPlane.SaveProjectAsync(
                new OperatorProjectState(string.Empty, trimmed, OperatorPipeline.Researching, "chat", string.Empty, string.Empty, [], null, []),
                origin,
                ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(NotStarted(trimmed, ex.Message), ex);
        }

        started.Writer.TryWrite(new QueuedProject(project, origin));
        return Started(project.Topic, origin is not null);
    }

    /// <summary>The topic and status of the latest video projects.</summary>
    /// <param name="ct">Cancels the request to the control plane.</param>
    /// <returns>One project per line, newest first, or a sentence when none exists or they cannot be read.</returns>
    [KernelFunction("youtube_video_status")]
    [Description("Topic and status of the five latest YouTube video projects, newest first: queued, researching, scripting, script_ready, or failed with the reason.")]
    public async Task<string> VideoStatusAsync(CancellationToken ct = default)
    {
        IReadOnlyList<OperatorProjectState> latest;
        try
        {
            latest = await controlPlane.ListRecentAsync(StatusCount, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return NoStatus(ex.Message);
        }

        return latest.Count == 0
            ? NoProjects
            : string.Join("\n", latest.Select(p => p.Error is null
                ? $"{p.Topic}: {p.Status}"
                : $"{p.Topic}: {p.Status} ({p.Error})"));
    }
}
