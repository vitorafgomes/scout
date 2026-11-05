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
    /// Take the oldest queued project moved to researching, else a project with a presenter request moved to
    /// presenting, before the production of the same project, else the oldest project queued for production moved to
    /// producing, else the oldest project queued for assembly moved to assembling, else one left unfinished 15 minutes;
    /// null when none waits.
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

    /// <summary>
    /// Claim the oldest YouTube action the owner approved that no write has recorded yet; null when none waits. A claim the
    /// worker did not record within 30 minutes is handed out again.
    /// </summary>
    Task<OperatorAction?> NextApprovedActionAsync(CancellationToken ct);

    /// <summary>
    /// Record that the worker ran <paramref name="action"/>, <c>succeeded</c> or <c>failed: </c> and why, which consumes it. A
    /// record the control plane already has returns normally. A grant the owner revoked after approving is a
    /// <see cref="WriteRevokedException"/>, any other refusal the control plane words an
    /// <see cref="InvalidOperationException"/>, and an unreachable control plane a <see cref="ControlPlaneUnavailableException"/>.
    /// </summary>
    Task RecordActionAsync(OperatorAction action, string outcome, CancellationToken ct);

    /// <summary>The comments on the owner's channel as the control plane holds them, newest first, with the ideas and the sync's state.</summary>
    Task<OperatorComments> GetCommentsAsync(CancellationToken ct);

    /// <summary>
    /// Save the worker's comments: every stored comment, the owner's fields and every time exactly as read, the worker's
    /// changes on top; the comments as stored are returned.
    /// </summary>
    Task<OperatorComments> SaveCommentsAsync(OperatorComments comments, CancellationToken ct);

    /// <summary>The absolute path of the folder for the bundled voices' samples, the one place the worker writes them.</summary>
    Task<string> GetVoiceSamplesDirectoryAsync(CancellationToken ct);

    /// <summary>From chat, for the owner: set a scene's text, which queues its narration again; the changed project is returned.</summary>
    Task<OperatorProjectState> EditSceneTextAsync(string projectId, string sceneId, string text, CancellationToken ct);

    /// <summary>From chat, for the owner: make a scene's narration again on the next production run.</summary>
    Task<OperatorProjectState> RegenerateNarrationAsync(string projectId, string sceneId, CancellationToken ct);

    /// <summary>From chat, for the owner: make a scene's narration and picture again on the next production run.</summary>
    Task<OperatorProjectState> RegenerateSceneAsync(string projectId, string sceneId, CancellationToken ct);

    /// <summary>From chat, for the owner: lock a scene so production leaves it as it is, or unlock it.</summary>
    Task<OperatorProjectState> SetSceneLockedAsync(string projectId, string sceneId, bool locked, CancellationToken ct);

    /// <summary>From chat, for the owner: put the scenes in <paramref name="sceneIds"/> order, every scene once.</summary>
    Task<OperatorProjectState> ReorderScenesAsync(string projectId, IReadOnlyList<string> sceneIds, CancellationToken ct);

    /// <summary>From chat, for the owner: queue production; an empty voice, direction or motion keeps the project's.</summary>
    Task<OperatorProjectState> ProduceProjectAsync(string projectId, string narrationVoice, string direction, string motion, CancellationToken ct);

    /// <summary>From chat, for the owner: delegate the project up to ready to approve; an empty voice, direction or motion keeps the project's.</summary>
    Task<OperatorProjectState> DelegateProjectAsync(string projectId, string narrationVoice, string direction, string motion, CancellationToken ct);

    /// <summary>
    /// From chat, for the owner: queue a new presenter with <paramref name="feedback"/> appended to the last prompt, or with
    /// <paramref name="prompt"/> as the base when given; both empty ask for another one.
    /// </summary>
    Task<OperatorProjectState> RequestPresenterAsync(string projectId, string feedback, string prompt, CancellationToken ct);
}

/// <summary>
/// The comments on the owner's channel, as the control plane's <c>CommentsView</c> message carries them. Every time is the
/// string the control plane sent and goes back exactly as read; the worker sets <see cref="SyncedAt"/> and
/// <see cref="SyncError"/> only for its own sync, and <see cref="SyncRequestedAt"/> is the owner's.
/// </summary>
public sealed record OperatorComments(
    IReadOnlyList<OperatorComment> Comments,
    IReadOnlyList<OperatorIdea> Ideas,
    string? SyncedAt,
    string? SyncRequestedAt,
    string? SyncError);

/// <summary>
/// One top-level comment a stranger wrote on the owner's channel, as the control plane's <c>AudienceComment</c> message
/// carries it. The worker sets the kind, the theme, the draft, the status it may move the comment to and, while it runs the
/// owner's approved reply or hold, the reply id and the error; <see cref="ApprovedText"/>, <see cref="DecidedAt"/> and
/// <see cref="FetchedAt"/> are the owner's or the control plane's and go back exactly as read, and a new comment leaves
/// <see cref="FetchedAt"/> empty.
/// </summary>
public sealed record OperatorComment(
    string Id,
    string ThreadId,
    string VideoId,
    string VideoTitle,
    string Author,
    string Text,
    string PublishedAt,
    long LikeCount,
    string Kind,
    string Theme,
    string Status,
    string Draft,
    string? ApprovedText,
    string? ReplyId,
    string? Error,
    string? DecidedAt,
    string FetchedAt);

/// <summary>A video idea drawn from a viewer's request, <c>proposed</c> when the worker adds it; the owner uses or dismisses it.</summary>
public sealed record OperatorIdea(string Id, string Text, string CommentId, string Status, string? ProjectId);

/// <summary>
/// The control plane could not be reached, as opposed to a refusal it worded; the call can succeed later unchanged.
/// </summary>
public sealed class ControlPlaneUnavailableException : InvalidOperationException
{
    /// <summary>Build the exception with the sentence the owner reads.</summary>
    public ControlPlaneUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// The control plane refused to record an action because the owner revoked the feature's writes after approving it; the
/// control plane logged the write "after revoke" and consumed the action.
/// </summary>
public sealed class WriteRevokedException : InvalidOperationException
{
    /// <summary>Build the exception with the control plane's words.</summary>
    public WriteRevokedException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// An irreversible action the owner approved on the Review screen, as the control plane's <c>PendingAction</c> carries it:
/// its catalog scope in <see cref="Action"/> (<c>videos.insert</c>, <c>videos.update</c>, <c>thumbnails.set</c>) and what it
/// acts on in <see cref="Target"/>; <see cref="OriginReference"/> names the chat the project came from, null for a project
/// the desktop started.
/// </summary>
public sealed record OperatorAction(string Id, string FeatureId, string Action, string Target, string Summary, string? OriginReference = null);

/// <summary>
/// A project's life on YouTube, as the control plane's <c>Publication</c> message carries it. The owner sets the privacy, the
/// publish time, the made-for-kids and synthetic-media declarations, the proposal decisions and the experiment's start and
/// arms; the worker sets the status, the video id, the upload time, the error, the snapshots, new proposals and the
/// experiment's timing, metrics, winner and result, and sends everything else back exactly as read.
/// </summary>
public sealed record OperatorPublication(
    string Status,
    string? VideoId,
    string PrivacyStatus,
    string? PublishAt,
    bool MadeForKids,
    bool SyntheticMedia,
    string? UploadedAt,
    string? Error,
    IReadOnlyList<OperatorSnapshot> Snapshots,
    IReadOnlyList<OperatorProposal> Proposals,
    OperatorExperiment? Experiment);

/// <summary>
/// A video's numbers over one window (<c>24h</c> or <c>7d</c>) as the Analytics API and the reach report returned them;
/// impressions and their click-through rate are null when no reach report covered the window.
/// </summary>
public sealed record OperatorSnapshot(
    string Window,
    string TakenAt,
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
    IReadOnlyList<OperatorRetentionPoint> Retention);

/// <summary>The share of the audience still watching at a share of the video's length.</summary>
public sealed record OperatorRetentionPoint(double ElapsedRatio, double AudienceWatchRatio);

/// <summary>
/// A change the worker proposes (<c>title</c>, <c>thumbnail</c>, <c>topic</c> or <c>strategy</c>), always
/// <c>proposed</c> when the worker makes it; the owner accepts or rejects it.
/// </summary>
public sealed record OperatorProposal(string Id, string Kind, string Text, string Reason, string Status, string? ThumbnailFile);

/// <summary>A title or thumbnail test the owner started: its arms, its status, and the worker's winner and result.</summary>
public sealed record OperatorExperiment(
    string Id,
    string Kind,
    string Status,
    IReadOnlyList<OperatorExperimentArm> Arms,
    string? WinnerArmId,
    string? Result);

/// <summary>
/// One arm of an experiment: the title, or the thumbnail's file name in the media directory, it puts live, when it went live
/// and ended, and the impressions, clicks and average view percentage collected for its window.
/// </summary>
public sealed record OperatorExperimentArm(
    string Id,
    string Label,
    string Value,
    string? StartedAt,
    string? EndedAt,
    long Impressions,
    long Clicks,
    double AverageViewPercentage);

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

    /// <summary>The assembled video and its metadata, null until the worker assembles it.</summary>
    public OperatorPackage? Package { get; init; }

    /// <summary>The video's publication on YouTube, null until the owner publishes the package.</summary>
    public OperatorPublication? Publication { get; init; }

    /// <summary>The owner's picture in the media directory every scene without its own picture is shown over, null until the owner attaches one.</summary>
    public string? BackgroundFile { get; init; }
    /// <summary>The owner's delegation of the project, sent back exactly as read; only the owner sets it.</summary>
    public bool Delegated { get; init; }

    /// <summary>
    /// The voice id the owner chose for this project's narration, one of the readiness <see cref="OperatorReadiness.Voices"/>,
    /// or empty for the worker's choice; sent back exactly as read, only the owner sets it.
    /// </summary>
    public string NarrationVoice { get; init; } = string.Empty;

    /// <summary>
    /// The owner's direction for the video, empty for none, which the script writer and the scene planner follow; sent
    /// back exactly as read, only the owner sets it.
    /// </summary>
    public string Direction { get; init; } = string.Empty;

    /// <summary>
    /// The motion preset the owner chose, <c>still</c>, <c>gentle</c> or <c>dynamic</c>, which the assembler applies;
    /// sent back exactly as read, only the owner sets it.
    /// </summary>
    public string Motion { get; init; } = "gentle";

    /// <summary>
    /// How the video is made, <c>generated_pictures</c>, <c>own_pictures</c>, <c>own_video</c> or <c>animated_character</c>,
    /// as the owner chose it when starting the project; sent back exactly as read, only the owner sets it, and anything else
    /// reads as generated pictures.
    /// </summary>
    public string Format { get; init; } = "generated_pictures";

    /// <summary>The owner's recording in the media directory a project of the owner's own video is made from, null until the owner attaches one; sent back exactly as read.</summary>
    public string? RecordingFile { get; init; }

    /// <summary>How long the owner's recording plays, in milliseconds, which the worker measures when it transcribes it; 0 until then.</summary>
    public int RecordingMs { get; init; }

    /// <summary>
    /// Who the animated character is and where they are, the owner's words, empty for none; sent back exactly as read, only
    /// the owner sets it.
    /// </summary>
    public string Presenter { get; init; } = string.Empty;

    /// <summary>
    /// The animated character's picture in the media directory, made once by the worker when empty or attached by the owner;
    /// null until then.
    /// </summary>
    public string? PresenterFile { get; init; }

    /// <summary>Where the mouth is on the presenter picture, which the owner marks; null until then, and the character is shown still.</summary>
    public OperatorMouthBox? MouthBox { get; init; }

    /// <summary>Every presenter the worker made for the project, oldest first; the worker appends one per presenter job.</summary>
    public IReadOnlyList<OperatorPresenterVersion> PresenterVersions { get; init; } = [];

    /// <summary>The current presenter's five-second preview clip in the media directory; null without one.</summary>
    public string? PresenterPreviewFile { get; init; }

    /// <summary>
    /// The owner's request for a new presenter: empty for none, <c>first</c> for the control plane's automatic one,
    /// <c>builtin:&lt;id&gt;</c> for a built-in presenter, else the owner's feedback; the worker clears it when it saves the job.
    /// </summary>
    public string PresenterRequest { get; init; } = string.Empty;

    /// <summary>Where the current presenter picture came from: <c>generated</c>, <c>builtin</c>, <c>owner</c>, or empty before any.</summary>
    public string PresenterSource { get; init; } = string.Empty;

    /// <summary>The status a <c>presenting</c> project goes back to once the presenter job is saved; sent back exactly as read.</summary>
    public string PresenterReturnStatus { get; init; } = string.Empty;

    /// <summary>The owner's own presenter prompt for the requested job, the base the feedback is appended to; empty to compose one.</summary>
    public string PresenterRequestPrompt { get; init; } = string.Empty;
}

/// <summary>
/// One presenter the worker made, as the control plane's <c>PresenterVersion</c> message carries it: the picture, the prompt
/// it was made from without the safety tail, the owner's feedback it answered, its preview clip, the mouth on it, when it was
/// made and where the picture came from.
/// </summary>
public sealed record OperatorPresenterVersion(
    string File,
    string Prompt,
    string Feedback,
    string? PreviewFile,
    OperatorMouthBox? MouthBox,
    string MadeAt,
    string Source);

/// <summary>A presenter Scout ships, as the control plane's <c>BuiltInPresenter</c> message carries it: its id and its label.</summary>
public sealed record OperatorBuiltInPresenter(string Id, string Label);

/// <summary>The mouth on the presenter picture, as the control plane's <c>MouthBox</c> message carries it: its left, top, width and height as fractions of the picture.</summary>
public sealed record OperatorMouthBox(double X, double Y, double Width, double Height);

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
public sealed record OperatorScriptSection(string Heading, string Narration)
{
    /// <summary>Where the section starts in the owner's recording, in milliseconds; 0 for a written script.</summary>
    public int StartMs { get; init; }

    /// <summary>Where the section ends in the owner's recording, in milliseconds; 0 for a written script.</summary>
    public int EndMs { get; init; }
}

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
    int DurationMs)
{
    /// <summary>The search terms the scene planner chose for a stock picture of the scene; empty when it chose none.</summary>
    public string VisualQuery { get; init; } = string.Empty;

    /// <summary>The attribution of a <c>stock</c> picture, which the video's description credits; null for any other visual.</summary>
    public string? VisualCredit { get; init; }

    /// <summary>Where the scene's clip starts in the owner's recording, in milliseconds, for a scene that shows the recording.</summary>
    public int ClipStartMs { get; init; }

    /// <summary>Where the scene's clip ends in the owner's recording, in milliseconds, for a scene that shows the recording.</summary>
    public int ClipEndMs { get; init; }
}

/// <summary>What the worker can produce, as the control plane's <c>Readiness</c> message carries it.</summary>
public sealed record OperatorReadiness(
    bool SpeechAvailable,
    bool ImagesAvailable,
    string Provider,
    bool FfmpegAvailable,
    bool YouTubeSignedIn,
    IReadOnlyList<string> Notes)
{
    /// <summary>The narration voices this machine offers, the connected AI Source's first, then the bundled ones.</summary>
    public IReadOnlyList<OperatorVoice> Voices { get; init; } = [];

    /// <summary>Whether a scene without an AI image service gets a freely licensed, credited picture from Openverse.</summary>
    public bool StockPictures { get; init; }

    /// <summary>Whether this machine has a speech-to-text model, so a project of the owner's own video can be transcribed.</summary>
    public bool TranscriptionAvailable { get; init; }

    /// <summary>Whether the rhubarb program answers and the nine mouth shapes are on disk, so an animated character's mouth moves with its narration.</summary>
    public bool LipSyncAvailable { get; init; }

    /// <summary>Whether both face models load in the local runtime, so the mouth on a presenter picture is found without the owner marking it.</summary>
    public bool FaceDetectionAvailable { get; init; }

    /// <summary>How many of the day's pictures from the AI Source are left in this process.</summary>
    public int PicturesLeftToday { get; init; }

    /// <summary>The presenters Scout ships, in the order of their list.</summary>
    public IReadOnlyList<OperatorBuiltInPresenter> BuiltInPresenters { get; init; } = [];
}

/// <summary>A narration voice, as the control plane's <c>Voice</c> message carries it: its id, its label, its source and its sample's file name in the voice samples directory, null when it has none.</summary>
public sealed record OperatorVoice(string Id, string Label, string Source, string? SampleFile = null);

/// <summary>
/// The assembled video, as the control plane's <c>VideoPackage</c> message carries it. Files are plain names inside the
/// project's media directory. The worker sets the status, the files, the checks, the duration and the error, and the
/// title, description and tags only while <see cref="MetadataEdited"/> is false; <see cref="RightsConfirmed"/>,
/// <see cref="ApprovedAt"/> and <see cref="MetadataEdited"/> are the owner's and go back exactly as read.
/// </summary>
public sealed record OperatorPackage(
    string Status,
    string? VideoFile,
    IReadOnlyList<OperatorShort> Shorts,
    string? ThumbnailFile,
    IReadOnlyList<OperatorCheck> Checks,
    bool RightsConfirmed,
    string? ApprovedAt,
    string Title,
    string Description,
    IReadOnlyList<string> Tags,
    int DurationMs,
    string? Error,
    bool MetadataEdited);

/// <summary>One vertical Short cut from the video: its file, where it starts in the video, its length and its hook.</summary>
public sealed record OperatorShort(string File, int StartMs, int DurationMs, string Hook);

/// <summary>One check of the package: its name, <c>pass</c>, <c>warn</c> or <c>fail</c>, and a sentence saying why.</summary>
public sealed record OperatorCheck(string Name, string Result, string Detail);
