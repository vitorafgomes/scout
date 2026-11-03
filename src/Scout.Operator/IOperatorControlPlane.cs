namespace Scout.Operator;

/// <summary>
/// The channel operator's state as the control plane holds it, reached with the worker's token. The control
/// plane is the single writer: it checks the owner's YouTube write grant on every call and refuses a change
/// only the owner may make. Every failure surfaces as an <see cref="InvalidOperationException"/> whose message
/// can be shown to the owner.
/// </summary>
public interface IOperatorControlPlane
{
    /// <summary>The owner's channel mandate; empty fields when the owner has not written one.</summary>
    Task<OperatorMandate> GetMandateAsync(CancellationToken ct);

    /// <summary>
    /// Save the worker's draft of a project. An empty <see cref="OperatorProjectState.Id"/> creates a project of
    /// origin chat that keeps <paramref name="originReference"/>; the saved project is returned.
    /// </summary>
    Task<OperatorProjectState> SaveProjectAsync(OperatorProjectState project, string? originReference, CancellationToken ct);

    /// <summary>
    /// Take the oldest queued project moved to researching, else the oldest project queued for production moved to
    /// producing, else one left unfinished 15 minutes; null when none waits.
    /// </summary>
    Task<QueuedProject?> NextQueuedAsync(CancellationToken ct);

    /// <summary>The project as the control plane holds it now, with the owner's claim decisions.</summary>
    Task<OperatorProjectState> GetProjectAsync(string id, CancellationToken ct);

    /// <summary>The newest <paramref name="limit"/> projects, newest first.</summary>
    Task<IReadOnlyList<OperatorProjectState>> ListRecentAsync(int limit, CancellationToken ct);

    /// <summary>The absolute path of the project's media directory, the one place the worker writes media files.</summary>
    Task<string> GetMediaDirectoryAsync(string projectId, CancellationToken ct);

    /// <summary>Tell the control plane what this worker can produce now.</summary>
    Task ReportReadinessAsync(OperatorReadiness readiness, CancellationToken ct);
}

/// <summary>The owner's brief for the channel, as the control plane's <c>Mandate</c> message carries it.</summary>
public sealed record OperatorMandate(
    string Objective,
    string Audience,
    IReadOnlyList<string> Pillars,
    string Cadence,
    string Language,
    string VoiceAndStyle,
    IReadOnlyList<string> BlockedTopics,
    string TargetMetric,
    string TargetValue,
    string? UpdatedAt = null);

/// <summary>One video project, as the control plane's <c>Project</c> message carries it.</summary>
public sealed record OperatorProjectState(
    string Id,
    string Topic,
    string Status,
    string Origin,
    string CreatedAt,
    string UpdatedAt,
    IReadOnlyList<OperatorTopicCandidate> Candidates,
    OperatorScript? Script,
    IReadOnlyList<OperatorClaim> Claims,
    string? Error = null)
{
    /// <summary>The scenes of the video, empty until the worker plans them.</summary>
    public IReadOnlyList<OperatorScene> Scenes { get; init; } = [];
}

/// <summary>A topic the research proposes, with why it fits the mandate and a fit from 0 to 1.</summary>
public sealed record OperatorTopicCandidate(string Title, string Reason, double Fit);

/// <summary>The script draft and its package metadata.</summary>
public sealed record OperatorScript(
    string Hook,
    IReadOnlyList<OperatorScriptSection> Sections,
    string CallToAction,
    IReadOnlyList<string> TitleVariants,
    string Description,
    IReadOnlyList<string> Tags,
    IReadOnlyList<OperatorChapter> Chapters);

/// <summary>One section of the script.</summary>
public sealed record OperatorScriptSection(string Heading, string Narration);

/// <summary>One chapter marker of the description.</summary>
public sealed record OperatorChapter(string At, string Title);

/// <summary>
/// A factual claim of the script, its risk (<c>standard</c> or <c>high</c>), its sources and its status:
/// <c>pending</c> until the owner marks it <c>supported</c>, <c>unsupported</c> or <c>waived</c>.
/// </summary>
public sealed record OperatorClaim(
    string Id,
    string Text,
    string Risk,
    IReadOnlyList<string> SourceUrls,
    string Status,
    string? Note = null);

/// <summary>A project to run and the reference of the chat conversation that asked for it, if any.</summary>
public sealed record QueuedProject(OperatorProjectState Project, string? OriginReference);

/// <summary>
/// One scene of the video, as the control plane's <c>Scene</c> message carries it. <see cref="AudioFile"/> and
/// <see cref="VisualFile"/> are plain file names inside the project's media directory. The worker never changes the
/// order, the lock, a locked scene, or media whose source is <c>owner</c>.
/// </summary>
public sealed record OperatorScene(
    string Id,
    int Order,
    string Narration,
    string VisualPrompt,
    bool Locked,
    string Status,
    string? Error,
    string AudioSource,
    string VisualSource,
    string? AudioFile,
    string? VisualFile,
    int DurationMs);

/// <summary>What the worker can produce, as the control plane's <c>Readiness</c> message carries it.</summary>
public sealed record OperatorReadiness(
    bool SpeechAvailable,
    bool ImagesAvailable,
    string Provider,
    bool FfmpegAvailable,
    bool YouTubeSignedIn,
    IReadOnlyList<string> Notes);
