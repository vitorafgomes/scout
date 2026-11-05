using System.Text.Json.Serialization;

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

    /// <summary>The owner's picture in the media directory that every scene without its own picture is shown over, null until the owner attaches one.</summary>
    public string? BackgroundFile { get; init; }
    /// <summary>
    /// True once the owner delegated the project: the control plane then chains its reversible stages, production and
    /// assembly, and stops at an assembled package for the owner to approve.
    /// </summary>
    public bool Delegated { get; init; }

    /// <summary>
    /// The voice the narration is made in, chosen by the owner when producing the project: a voice id from the
    /// readiness, or empty for Scout's choice. Only the owner's produce sets it.
    /// </summary>
    public string NarrationVoice { get; init; } = string.Empty;

    /// <summary>
    /// The owner's direction for the video, at most 1000 characters, empty for none, given when the project is started,
    /// produced or delegated; the script writer and the scene planner follow it. Stored under the key of the visual brief
    /// it replaced, so a projects file written before keeps it. Only the owner sets it.
    /// </summary>
    [JsonPropertyName("visualBrief")]
    public string Direction { get; init; } = string.Empty;

    /// <summary>
    /// The motion preset the video is assembled with, <c>still</c>, <c>gentle</c> or <c>dynamic</c>, chosen with the
    /// direction. Only the owner's produce or delegate sets it.
    /// </summary>
    public string Motion { get; init; } = "gentle";

    /// <summary>
    /// How the video is made, <c>generated_pictures</c>, <c>own_pictures</c>, <c>own_video</c> or
    /// <c>animated_character</c>, chosen when the owner starts the project and never changed after; a project stored
    /// before formats existed is generated pictures.
    /// </summary>
    public string Format { get; init; } = "generated_pictures";

    /// <summary>
    /// The owner's recording in the media directory, the whole content of a project of the owner's own video; null until
    /// the owner attaches it. Only the owner sets it.
    /// </summary>
    public string? RecordingFile { get; init; }

    /// <summary>The length of the recording in milliseconds, 0 until the worker measures it while transcribing.</summary>
    public int RecordingMs { get; init; }

    /// <summary>
    /// Who the animated character is and where they are, at most 300 characters, empty for none, given when the project
    /// is started, produced or delegated. Only the owner sets it.
    /// </summary>
    public string Presenter { get; init; } = string.Empty;

    /// <summary>
    /// The presenter picture in the media directory of a project of an animated character, null until the owner attaches
    /// one or the worker records the one it made while none is set; the worker never replaces or clears it.
    /// </summary>
    public string? PresenterFile { get; init; }

    /// <summary>Where the character's mouth is on the presenter picture, marked by the owner; null until marked.</summary>
    public MouthBox? MouthBox { get; init; }
}

/// <summary>A rectangle on the presenter picture, each value a fraction of the picture's width or height.</summary>
public sealed record MouthBox(double X, double Y, double Width, double Height);

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
public sealed record ScriptSection(string Heading, string Narration)
{
    /// <summary>Where the section starts in the owner's recording, in milliseconds; 0 for a written script.</summary>
    public int StartMs { get; init; }

    /// <summary>Where the section ends in the owner's recording, in milliseconds; 0 for a written script.</summary>
    public int EndMs { get; init; }
}

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
    int DurationMs)
{
    /// <summary>The search terms the worker planned for a stock picture of the scene; empty when it planned none.</summary>
    public string VisualQuery { get; init; } = string.Empty;

    /// <summary>The attribution of a <c>stock</c> picture, which the video's description credits; null for any other visual.</summary>
    public string? VisualCredit { get; init; }

    /// <summary>Where the scene's clip of the owner's recording starts, in milliseconds; 0 for a scene of another source.</summary>
    public int ClipStartMs { get; init; }

    /// <summary>Where the scene's clip of the owner's recording ends, in milliseconds; 0 for a scene of another source.</summary>
    public int ClipEndMs { get; init; }
}

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
    DateTimeOffset? ReportedAt = null)
{
    /// <summary>The narration voices the worker offers, the connected AI Source's first, then the bundled ones.</summary>
    public IReadOnlyList<Voice> Voices { get; init; } = [];

    /// <summary>Whether a scene without an AI image service gets a freely licensed, credited picture from Openverse.</summary>
    public bool StockPictures { get; init; }

    /// <summary>Whether the worker has a speech-to-text model, so a project of the owner's own video can be transcribed.</summary>
    public bool TranscriptionAvailable { get; init; }

    /// <summary>Whether the worker has the lip sync program and the mouth shapes, so an animated character speaks.</summary>
    public bool LipSyncAvailable { get; init; }
}

/// <summary>A narration voice: the id a mandate names, the label the owner reads, where it comes from and its sample's file name in the voice samples directory, null when it has none.</summary>
public sealed record Voice(string Id, string Label, string Source, string? SampleFile = null);

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
