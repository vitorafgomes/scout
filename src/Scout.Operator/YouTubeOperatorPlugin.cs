using System.ComponentModel;
using System.Globalization;
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
    internal const string PublishVideo = "youtube_publish_video";
    internal const string NoProjects = "No video project exists yet.";

    internal const int StatusCount = 5;

    internal static string Started(string topic, bool postsBack) => postsBack
        ? $"Started a video project on {topic}; I will post here when the script is ready."
        : $"Started a video project on {topic}; follow it in Scout Desktop, Production.";

    internal static string NotStarted(string topic, string reason) =>
        $"Could not start a video project on {topic}: {reason}";

    internal static string NoStatus(string reason) => $"Could not read the video projects: {reason}";

    internal static string PublishOnDesktop(string project) =>
        $"Publishing {project} happens on the Assembly page of Scout Desktop's YouTube operator, where the owner chooses its privacy; Scout uploads nothing from chat.";

    internal static string UploadedNotice(string topic, string privacy, string videoId) =>
        $"Uploaded {topic} as {privacy}: {Link(videoId)}";

    internal static string ScheduledNotice(string topic, DateTimeOffset publishAt, string videoId) =>
        $"Scheduled {topic} for {London.Format(publishAt)}: {Link(videoId)}";

    internal static string Link(string videoId) => $"https://youtu.be/{videoId}";

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

    /// <summary>Say where a finished video project is published; it changes nothing and queues nothing.</summary>
    /// <param name="project">The project's topic or id.</param>
    /// <returns>Where the owner publishes the video, in a sentence for the user.</returns>
    [KernelFunction(PublishVideo)]
    [Description("Explain how to publish a finished YouTube video project. Scout never uploads from chat: the owner publishes on the Assembly page of Scout Desktop's YouTube operator, choosing privacy and audience there.")]
    public static Task<string> PublishVideoAsync(
        [Description("The video project's topic.")] string project)
        => Task.FromResult(PublishOnDesktop((project ?? string.Empty).Trim()));

    /// <summary>The topic and status of the latest video projects.</summary>
    /// <param name="ct">Cancels the request to the control plane.</param>
    /// <returns>One project per line, newest first, or a sentence when none exists or they cannot be read.</returns>
    [KernelFunction("youtube_video_status")]
    [Description("Topic and status of the five latest YouTube video projects, newest first: queued, researching, scripting, script_ready, production_queued, producing with how many scenes are ready, media_ready, failed with the reason, or, once published, the upload, its link and the numbers of its last snapshot.")]
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
            : string.Join("\n", latest.Select(Line));
    }

    private static string Line(OperatorProjectState p)
    {
        if (p.Publication is { } publication)
        {
            return $"{p.Topic}: {PublicationLine(p.Topic, publication)}";
        }

        if (p.Error is not null)
        {
            return $"{p.Topic}: {p.Status} ({p.Error})";
        }

        if (p.Status != OperatorPipeline.Producing || p.Scenes.Count == 0)
        {
            return $"{p.Topic}: {p.Status}";
        }

        var ready = p.Scenes.Count(s => s.Status == OperatorPipeline.Ready);
        var failed = p.Scenes.Count(s => s.Status == OperatorPipeline.Failed);
        return failed == 0
            ? $"{p.Topic}: {p.Status} ({ready} of {p.Scenes.Count} scenes ready)"
            : $"{p.Topic}: {p.Status} ({ready} of {p.Scenes.Count} scenes ready, {failed} failed)";
    }

    private static string PublicationLine(string topic, OperatorPublication publication)
    {
        var line = (publication.Status, publication.VideoId) switch
        {
            ("failed", _) => $"upload failed ({publication.Error})",
            ("scheduled", { } id) when publication.PublishAt is { } at
                && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) => ScheduledNotice(topic, when, id),
            ("published", { } id) => $"Published {topic}: {Link(id)}",
            (_, { } id) => UploadedNotice(topic, publication.PrivacyStatus, id),
            _ => publication.Error is null ? publication.Status : $"{publication.Status} ({publication.Error})",
        };
        if (publication.Snapshots.Count == 0)
        {
            return line;
        }

        var last = publication.Snapshots[^1];
        var numbers = string.Create(
            CultureInfo.InvariantCulture,
            $"{line}; first {last.Window}: {last.Views:N0} views, {last.AverageViewPercentage:0.0}% average view percentage");
        return last is { Impressions: { } impressions, ImpressionsCtr: { } ctr }
            ? string.Create(CultureInfo.InvariantCulture, $"{numbers}, {ctr * 100:0.0}% click-through rate on {impressions:N0} impressions")
            : numbers;
    }
}
