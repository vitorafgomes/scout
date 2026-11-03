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
