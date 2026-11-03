namespace Scout.ControlPlane.Operator;

/// <summary>The owner's brief for the channel: what it is for, who watches it and how it sounds.</summary>
public sealed record Mandate(
    string Objective,
    string Audience,
    IReadOnlyList<string> Pillars,
    string Cadence,
    string Language,
    string VoiceAndStyle,
    IReadOnlyList<string> BlockedTopics,
    string TargetMetric,
    string TargetValue,
    DateTimeOffset? UpdatedAt = null)
{
    /// <summary>The mandate before the owner wrote one.</summary>
    public static Mandate Empty { get; } = new(
        string.Empty, string.Empty, [], string.Empty, string.Empty, string.Empty, [], string.Empty, string.Empty);
}

/// <summary>
/// One video the operator works on. <c>OriginReference</c> is the opaque reference to the chat
/// conversation that asked for it; only the worker ever receives it.
/// </summary>
public sealed record OperatorProject(
    string Id,
    string Topic,
    string Status,
    string Origin,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<TopicCandidate> Candidates,
    Script? Script,
    IReadOnlyList<Claim> Claims,
    string? Error = null,
    string? OriginReference = null)
{
    /// <summary>The scenes of the video, empty until the worker plans them.</summary>
    public IReadOnlyList<Scene> Scenes { get; init; } = [];

    /// <summary>The assembled video and its metadata, null until the owner first asks for assembly.</summary>
    public VideoPackage? Package { get; init; }

    /// <summary>The project's life on YouTube, null until the owner publishes the approved package.</summary>
    public Publication? Publication { get; init; }
}

/// <summary>A topic the research stage proposes, with why it fits the mandate.</summary>
public sealed record TopicCandidate(string Title, string Reason, double Fit);

/// <summary>The script draft and its package metadata.</summary>
public sealed record Script(
    string Hook,
    IReadOnlyList<ScriptSection> Sections,
    string CallToAction,
    IReadOnlyList<string> TitleVariants,
    string Description,
    IReadOnlyList<string> Tags,
    IReadOnlyList<Chapter> Chapters);

/// <summary>One section of the script.</summary>
public sealed record ScriptSection(string Heading, string Narration);

/// <summary>One chapter marker of the video description.</summary>
public sealed record Chapter(string At, string Title);

/// <summary>
/// A factual claim of the script. <c>DecidedAt</c> is set only when the owner decided the claim;
/// the worker can never change or drop such a claim.
/// </summary>
public sealed record Claim(
    string Id,
    string Text,
    string Risk,
    IReadOnlyList<string> SourceUrls,
    string Status,
    string? Note = null,
    DateTimeOffset? DecidedAt = null);

/// <summary>
/// One scene of the video. <c>AudioFile</c> and <c>VisualFile</c> are plain file names inside the
/// project's media directory; a source of <c>owner</c> marks media the owner attached, which the
/// worker can never replace.
/// </summary>
public sealed record Scene(
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

/// <summary>
/// The assembled video of a project: the MP4, its Shorts and thumbnail as plain file names inside
/// the project's media directory, the checks the worker ran, and the upload metadata. Only the owner
/// confirms the media rights, approves the package or edits its metadata; once the owner edited it,
/// the worker keeps the title, description and tags as they are.
/// </summary>
public sealed record VideoPackage(
    string Status,
    string? VideoFile,
    IReadOnlyList<ShortClip> Shorts,
    string? ThumbnailFile,
    IReadOnlyList<PackageCheck> Checks,
    bool RightsConfirmed,
    DateTimeOffset? ApprovedAt,
    string Title,
    string Description,
    IReadOnlyList<string> Tags,
    int DurationMs,
    string? Error,
    bool MetadataEdited);

/// <summary>One vertical Shorts cut of the video, from <c>StartMs</c> into it, with its hook line.</summary>
public sealed record ShortClip(string File, int StartMs, int DurationMs, string Hook);

/// <summary>One check the worker ran on the package; <c>Result</c> is pass, warn or fail.</summary>
public sealed record PackageCheck(string Name, string Result, string Detail);

/// <summary>What the worker reported it can produce, stamped by the control plane when it arrived.</summary>
public sealed record Readiness(
    bool SpeechAvailable,
    bool ImagesAvailable,
    string Provider,
    bool FfmpegAvailable,
    bool YouTubeSignedIn,
    IReadOnlyList<string> Notes,
    DateTimeOffset? ReportedAt = null);

/// <summary>
/// A project's life on YouTube. The owner sets the privacy, the publish time, the made-for-kids and synthetic-media
/// declarations, the proposal decisions and the experiment's start and arms; the worker sets the status after
/// <c>publish_approved</c>, the video id once, the upload time, the error, the snapshots, new proposals and the
/// running experiment's timing, metrics, winner and result.
/// </summary>
public sealed record Publication(
    string Status,
    string? VideoId,
    string PrivacyStatus,
    DateTimeOffset? PublishAt,
    bool MadeForKids,
    bool SyntheticMedia,
    DateTimeOffset? UploadedAt,
    string? Error,
    IReadOnlyList<Snapshot> Snapshots,
    IReadOnlyList<Proposal> Proposals,
    Experiment? Experiment);

/// <summary>A video's numbers over one window; impressions and their click-through rate are null without a reach report.</summary>
public sealed record Snapshot(
    string Window,
    DateTimeOffset TakenAt,
    long Views,
    double MinutesWatched,
    double AverageViewDurationSeconds,
    double AverageViewPercentage,
    long SubscribersGained,
    long SubscribersLost,
    long Likes,
    long Comments,
    long Shares,
    long? Impressions,
    double? ImpressionsCtr,
    IReadOnlyList<RetentionPoint> Retention);

/// <summary>The share of the audience still watching at a share of the video's length.</summary>
public sealed record RetentionPoint(double ElapsedRatio, double AudienceWatchRatio);

/// <summary>A change the worker proposes, <c>proposed</c> until the owner accepts or rejects it.</summary>
public sealed record Proposal(string Id, string Kind, string Text, string Reason, string Status, string? ThumbnailFile);

/// <summary>
/// A title or thumbnail test the owner started. <c>ArmHours</c> is how long each arm stays live; only the control
/// plane keeps it.
/// </summary>
public sealed record Experiment(
    string Id,
    string Kind,
    string Status,
    IReadOnlyList<ExperimentArm> Arms,
    string? WinnerArmId,
    string? Result,
    int ArmHours = 48);

/// <summary>One arm of an experiment: the title, or the thumbnail file name, it puts live, its window and its numbers.</summary>
public sealed record ExperimentArm(
    string Id,
    string Label,
    string Value,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    long Impressions,
    long Clicks,
    double AverageViewPercentage);
