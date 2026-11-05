namespace Scout.ControlPlane.Operator;

/// <summary>
/// The scene vocabulary, the rule every media file name meets, and what the worker may change
/// in a project's scene list.
/// </summary>
internal static class SceneRules
{
    public const string Owner = "owner";
    public const string NoAudio = "none";
    public const string TextCard = "text_card";
    public const string Stock = "stock";
    public const string Planned = "planned";
    public const string Ready = "ready";
    public const string Audio = "audio";
    public const string Visual = "visual";
    public const string Video = "video";
    public const string Thumbnail = "thumbnail";
    public const string Recording = "recording";
    public const string Presenter = "presenter";

    private const int MaxFileNameLength = 120;

    private static readonly string[] Statuses = [Planned, "generating", Ready, "failed"];
    private static readonly string[] AudioSources = ["generated", Owner, NoAudio, Recording];
    private static readonly string[] VisualSources = ["generated", Owner, TextCard, Stock, Recording, Presenter];
    private static readonly string[] AudioExtensions = [".mp3", ".wav", ".m4a", ".ogg"];
    private static readonly string[] VisualExtensions = [".png", ".jpg", ".jpeg", ".webp"];
    private static readonly string[] VideoExtensions = [".mp4"];
    private static readonly string[] ThumbnailExtensions = [".png", ".jpg"];
    private static readonly string[] RecordingExtensions = [".mp4", ".mkv", ".webm", ".mov"];

    /// <summary>Refuse a media file name that is not a plain name of the kind's extensions: audio, visual, video, thumbnail or recording.</summary>
    /// <exception cref="OperatorValidationException">The name is empty, too long, hidden, a path, or of another type.</exception>
    public static void ValidateFileName(string? name, string kind)
    {
        var (noun, extensions) = kind switch
        {
            Audio => ("an audio", AudioExtensions),
            Video => ("a video", VideoExtensions),
            Thumbnail => ("a thumbnail", ThumbnailExtensions),
            Recording => ("a recording", RecordingExtensions),
            _ => ("a visual", VisualExtensions),
        };
        if (string.IsNullOrEmpty(name)
            || name.Length > MaxFileNameLength
            || name.StartsWith('.')
            || name.Contains("..", StringComparison.Ordinal)
            || name.Contains('\\', StringComparison.Ordinal)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !extensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
        {
            throw new OperatorValidationException(
                $"'{name}' is not {noun} file Scout accepts: a plain file name of at most {MaxFileNameLength} characters, "
                + $"without a folder or a leading dot, ending in {string.Join(", ", extensions)}.");
        }
    }

    /// <summary>Refuse scenes with a missing or repeated id, a value outside the vocabulary, a stock picture without its file or credit, a scene of the recording without a clip that ends after it starts, or an unsafe file name.</summary>
    /// <exception cref="OperatorValidationException">The first rule a scene breaks.</exception>
    public static void Validate(IReadOnlyList<Scene> scenes)
    {
        if (scenes.Any(s => string.IsNullOrWhiteSpace(s.Id)))
        {
            throw new OperatorValidationException("Every scene needs an id.");
        }
        if (scenes.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != scenes.Count)
        {
            throw new OperatorValidationException("Scene ids must be unique within a project.");
        }

        foreach (var scene in scenes)
        {
            if (!Statuses.Contains(scene.Status, StringComparer.Ordinal)
                || !AudioSources.Contains(scene.AudioSource, StringComparer.Ordinal)
                || !VisualSources.Contains(scene.VisualSource, StringComparer.Ordinal)
                || scene.DurationMs < 0)
            {
                throw new OperatorValidationException(
                    $"Scene '{scene.Id}' has a status, source or duration the operator does not accept.");
            }
            if (scene.VisualSource == Stock && (scene.VisualFile is null || string.IsNullOrWhiteSpace(scene.VisualCredit)))
            {
                throw new OperatorValidationException(
                    $"Scene '{scene.Id}' shows a stock picture; it needs the picture's file and its credit.");
            }
            if ((scene.AudioSource == Recording || scene.VisualSource == Recording)
                && (scene.ClipStartMs < 0 || scene.ClipEndMs <= scene.ClipStartMs))
            {
                throw new OperatorValidationException(
                    $"Scene '{scene.Id}' shows the recording; it needs a clip that ends after it starts.");
            }
            if (scene.AudioFile is not null)
            {
                ValidateFileName(scene.AudioFile, Audio);
            }
            if (scene.VisualFile is not null)
            {
                ValidateFileName(scene.VisualFile, Visual);
            }
        }
    }

    /// <summary>
    /// The scene list after a worker save: the worker's scenes when the project had none, else the
    /// stored scenes in their order with what the worker may set taken from <paramref name="incoming"/>.
    /// What the owner owns comes from <paramref name="stored"/> whatever the worker sends: the order,
    /// the lock, a locked scene whole, the scene's narration, and the owner's audio or visual with its file.
    /// </summary>
    /// <exception cref="OperatorTamperingException">The worker adds or drops a scene, or claims the owner's media or lock.</exception>
    public static IReadOnlyList<Scene> MergeFromWorker(string projectId, IReadOnlyList<Scene> stored, IReadOnlyList<Scene> incoming)
    {
        if (stored.Count == 0)
        {
            foreach (var scene in incoming)
            {
                if (scene.Locked)
                {
                    throw new OperatorTamperingException($"Only the owner locks a scene; scene '{scene.Id}' was not.");
                }
                RefuseOwnerMedia(scene, before: null);
            }
            return incoming;
        }

        if (incoming.Count != stored.Count
            || incoming.Any(s => !stored.Any(t => string.Equals(t.Id, s.Id, StringComparison.Ordinal))))
        {
            throw new OperatorTamperingException(
                $"Project '{projectId}' has its scenes; the worker may not add or drop one.");
        }

        return stored.Select(before =>
        {
            if (before.Locked)
            {
                return before;
            }

            var sent = incoming.First(s => string.Equals(s.Id, before.Id, StringComparison.Ordinal));
            RefuseOwnerMedia(sent, before);
            var merged = sent with { Order = before.Order, Locked = before.Locked, Narration = before.Narration };
            if (before.AudioSource == Owner)
            {
                var sameAudio = sent.AudioSource == Owner && string.Equals(sent.AudioFile, before.AudioFile, StringComparison.Ordinal);
                merged = merged with
                {
                    AudioSource = Owner,
                    AudioFile = before.AudioFile,
                    DurationMs = sameAudio ? sent.DurationMs : before.DurationMs,
                };
            }
            if (before.VisualSource == Owner)
            {
                merged = merged with { VisualSource = Owner, VisualFile = before.VisualFile, VisualCredit = null };
            }
            return merged;
        }).ToList();
    }

    private static void RefuseOwnerMedia(Scene sent, Scene? before)
    {
        if (before?.AudioSource != Owner && sent.AudioSource == Owner)
        {
            throw new OperatorTamperingException($"Only the owner attaches their own {Audio}; scene '{sent.Id}' has none.");
        }
        if (before?.VisualSource != Owner && sent.VisualSource == Owner)
        {
            throw new OperatorTamperingException($"Only the owner attaches their own {Visual}; scene '{sent.Id}' has none.");
        }
    }
}
