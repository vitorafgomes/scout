using System.Text.RegularExpressions;

namespace Scout.ControlPlane.Operator;

/// <summary>
/// The package vocabulary, the rules a package the worker sends meets, the owner's metadata limits
/// (YouTube's), the metadata and discoverability checks of edited metadata, and what the worker may
/// change in a package.
/// </summary>
internal static partial class PackageRules
{
    public const string AssemblyQueued = "assembly_queued";
    public const string Assembling = "assembling";
    public const string Assembled = "assembled";
    public const string Approved = "approved";
    public const string Fail = "fail";

    private const int MaxTitleLength = 100;
    private const int MaxDescriptionLength = 5000;
    private const int MaxTagLength = 100;
    private const int MaxTagsLength = 500;
    private const int MaxThumbnailDescriptionLength = 300;

    private const string MetadataCheck = "metadata";
    private const string DiscoverabilityCheck = "discoverability";

    private static readonly string[] Statuses = [AssemblyQueued, Assembling, Assembled, Approved, "failed"];
    private static readonly string[] OwnerStatuses = [AssemblyQueued, Approved];
    private static readonly string[] CheckResults = ["pass", "warn", Fail];

    /// <summary>
    /// The package a new assembly starts from: nothing assembled, no rights confirmed, not approved,
    /// and the owner's metadata, with when the owner saved it, kept only when the owner edited it.
    /// </summary>
    public static VideoPackage Queue(VideoPackage? before) =>
        before is { MetadataEdited: true }
            ? new VideoPackage(AssemblyQueued, null, [], null, [], false, null, before.Title, before.Description, before.Tags, 0, null, true)
            {
                MetadataSavedAt = before.MetadataSavedAt,
            }
            : new VideoPackage(AssemblyQueued, null, [], null, [], false, null, string.Empty, string.Empty, [], 0, null, false);

    /// <summary>
    /// Refuse a package with a status, check result, duration or file name the operator does not accept,
    /// or with metadata outside YouTube's limits once any of it is written.
    /// </summary>
    /// <exception cref="OperatorValidationException">The first rule the package breaks.</exception>
    public static void Validate(VideoPackage package)
    {
        if (!Statuses.Contains(package.Status, StringComparer.Ordinal))
        {
            throw new OperatorValidationException(
                $"Package status '{package.Status}' is not one the operator accepts: {string.Join(", ", Statuses)}.");
        }
        if (package.DurationMs < 0 || package.Shorts.Any(s => s.StartMs < 0 || s.DurationMs < 0))
        {
            throw new OperatorValidationException("A package or Shorts duration, or a Shorts start, is below 0.");
        }
        if (package.Checks.Any(c => string.IsNullOrWhiteSpace(c.Name)))
        {
            throw new OperatorValidationException("Every check needs a name.");
        }

        var unknown = package.Checks.FirstOrDefault(c => !CheckResults.Contains(c.Result, StringComparer.Ordinal));
        if (unknown is not null)
        {
            throw new OperatorValidationException(
                $"Check '{unknown.Name}' has a result the operator does not accept: {string.Join(", ", CheckResults)}.");
        }

        if (package.Title.Length > 0 || package.Description.Length > 0 || package.Tags.Count > 0)
        {
            ValidateMetadata(package.Title, package.Description, package.Tags);
        }
        if (package.VideoFile is not null)
        {
            SceneRules.ValidateFileName(package.VideoFile, SceneRules.Video);
        }
        if (package.ThumbnailFile is not null)
        {
            SceneRules.ValidateFileName(package.ThumbnailFile, SceneRules.Thumbnail);
        }
        foreach (var clip in package.Shorts)
        {
            SceneRules.ValidateFileName(clip.File, SceneRules.Video);
        }
    }

    /// <summary>
    /// The package after a worker save: the stored one when the worker sends none, else the worker's
    /// with the stored times of the owner's metadata save and rights confirmation, whatever it sent.
    /// The worker never confirms the rights, approves, queues an assembly, marks the metadata edited,
    /// or changes the title, description or tags once the owner edited them.
    /// </summary>
    /// <exception cref="OperatorTamperingException">The worker changes what only the owner sets.</exception>
    /// <exception cref="OperatorValidationException">An assembly time on a package still queued or assembling, or a thumbnail description over 300 characters.</exception>
    public static VideoPackage? MergeFromWorker(string projectId, VideoPackage? stored, VideoPackage? incoming)
    {
        if (incoming is null)
        {
            return stored;
        }
        if (incoming.RightsConfirmed != (stored?.RightsConfirmed ?? false))
        {
            throw new OperatorTamperingException($"Only the owner confirms the media rights of project '{projectId}'.");
        }
        if (incoming.ApprovedAt != stored?.ApprovedAt)
        {
            throw new OperatorTamperingException($"Only the owner approves the package of project '{projectId}'.");
        }
        if (OwnerStatuses.Contains(incoming.Status, StringComparer.Ordinal)
            && !string.Equals(incoming.Status, stored?.Status, StringComparison.Ordinal))
        {
            throw new OperatorTamperingException(
                $"Only the owner sets the package of project '{projectId}' to {incoming.Status}.");
        }
        if (incoming.MetadataEdited != (stored?.MetadataEdited ?? false))
        {
            throw new OperatorTamperingException($"Only the owner edits the package metadata of project '{projectId}'.");
        }
        if (stored is { MetadataEdited: true }
            && (!string.Equals(incoming.Title, stored.Title, StringComparison.Ordinal)
                || !string.Equals(incoming.Description, stored.Description, StringComparison.Ordinal)
                || !incoming.Tags.SequenceEqual(stored.Tags, StringComparer.Ordinal)))
        {
            throw new OperatorTamperingException(
                $"The owner edited the package metadata of project '{projectId}'; the worker may not change it.");
        }

        if (incoming.AssembledAt is not null && incoming.Status is AssemblyQueued or Assembling)
        {
            throw new OperatorValidationException($"A package that is {incoming.Status} has no assembly time yet.");
        }
        if (incoming.ThumbnailDescription is { Length: > MaxThumbnailDescriptionLength })
        {
            throw new OperatorValidationException(
                $"The thumbnail description is longer than {MaxThumbnailDescriptionLength} characters.");
        }

        return incoming with { MetadataSavedAt = stored?.MetadataSavedAt, RightsConfirmedAt = stored?.RightsConfirmedAt };
    }

    /// <summary>Whether two packages hold the same values, their lists compared item by item.</summary>
    public static bool Same(VideoPackage? a, VideoPackage? b)
    {
        if (a is null || b is null)
        {
            return ReferenceEquals(a, b);
        }

        return a with { Shorts = b.Shorts, Checks = b.Checks, Tags = b.Tags } == b
            && a.Shorts.SequenceEqual(b.Shorts)
            && a.Checks.SequenceEqual(b.Checks)
            && a.Tags.SequenceEqual(b.Tags, StringComparer.Ordinal);
    }

    /// <summary>Refuse an assembled package without its video, or naming a file not in <paramref name="directory"/>.</summary>
    /// <exception cref="OperatorValidationException">The video file is missing from the package or a named file from the folder.</exception>
    public static void RequireFiles(string directory, VideoPackage? package)
    {
        if (package?.VideoFile is null)
        {
            throw new OperatorValidationException("An assembled package needs its video file.");
        }

        var names = new[] { package.VideoFile, package.ThumbnailFile }.Concat(package.Shorts.Select(s => s.File)).OfType<string>();
        var missing = names.FirstOrDefault(name => !File.Exists(Path.Combine(directory, name)));
        if (missing is not null)
        {
            throw new OperatorValidationException($"'{missing}' is not in the project's media folder.");
        }
    }

    /// <summary>Refuse metadata outside YouTube's limits, naming every limit it breaks.</summary>
    /// <exception cref="OperatorValidationException">The title, description or tags break their limits.</exception>
    public static void ValidateMetadata(string title, string description, IReadOnlyList<string> tags)
    {
        var errors = MetadataErrors(title, description, tags);
        if (errors.Count > 0)
        {
            throw new OperatorValidationException(string.Join(" ", errors));
        }
    }

    /// <summary>
    /// The package after an owner's scene edit that changes the video: an assembled or approved package
    /// goes back to assembled without its rights confirmation, its time, or approval, one queued for assembly or
    /// assembling goes back to queued for assembly; any other package stays as it is.
    /// </summary>
    public static VideoPackage? AfterSceneEdit(VideoPackage? package) =>
        package switch
        {
            { Status: Assembled or Approved } => package with { Status = Assembled, RightsConfirmed = false, RightsConfirmedAt = null, ApprovedAt = null },
            { Status: AssemblyQueued or Assembling } => package with { Status = AssemblyQueued },
            _ => package,
        };

    /// <summary>
    /// <paramref name="checks"/> with the metadata and discoverability checks run again over the
    /// metadata, the worker's rules: metadata fails outside YouTube's limits; discoverability warns when
    /// the title shares no word of three letters or more with the topic or the pillars, or when the
    /// description has no chapters list starting at 0:00.
    /// </summary>
    public static IReadOnlyList<PackageCheck> WithMetadataChecks(
        IReadOnlyList<PackageCheck> checks,
        string title,
        string description,
        IReadOnlyList<string> tags,
        string topic,
        IReadOnlyList<string> pillars)
    {
        var errors = MetadataErrors(title, description, tags);
        var metadata = errors.Count > 0
            ? new PackageCheck(MetadataCheck, Fail, $"YouTube would refuse this metadata: {string.Join(" ", errors)}")
            : new PackageCheck(MetadataCheck, "pass", "The title, description and tags fit YouTube's limits.");

        var warnings = new List<string>();
        if (!Words(title).Overlaps(Words(string.Join(' ', pillars.Prepend(topic)))))
        {
            warnings.Add("the title shares no word with the topic or the channel's pillars");
        }
        if (!ChapterLine().IsMatch(description))
        {
            warnings.Add("the description has no chapters list");
        }

        var discoverability = warnings.Count > 0
            ? new PackageCheck(DiscoverabilityCheck, "warn", $"Viewers may not find this video: {string.Join("; ", warnings)}.")
            : new PackageCheck(DiscoverabilityCheck, "pass", "The title matches the topic and the description lists chapters.");
        return [.. checks.Where(c => c.Name is not (MetadataCheck or DiscoverabilityCheck)), metadata, discoverability];
    }

    /// <summary>How YouTube counts tags: their characters, two more for a tag with a space, and a comma between tags.</summary>
    public static int TagsLength(IReadOnlyList<string> tags) =>
        tags.Sum(t => t.Length + (t.Contains(' ', StringComparison.Ordinal) ? 2 : 0)) + Math.Max(0, tags.Count - 1);

    /// <summary>One sentence per YouTube limit the metadata breaks, none when it keeps them all.</summary>
    public static List<string> MetadataErrors(string title, string description, IReadOnlyList<string> tags)
    {
        var errors = new List<string>();
        if (title.Length is 0 or > MaxTitleLength)
        {
            errors.Add($"The title is 1 to {MaxTitleLength} characters.");
        }
        if (description.Length > MaxDescriptionLength)
        {
            errors.Add($"The description is at most {MaxDescriptionLength} characters.");
        }
        if (tags.Any(t => t.Length is 0 or > MaxTagLength))
        {
            errors.Add($"Each tag is 1 to {MaxTagLength} characters.");
        }
        if (TagsLength(tags) > MaxTagsLength)
        {
            errors.Add($"The tags are at most {MaxTagsLength} characters together.");
        }

        return errors;
    }

    private static HashSet<string> Words(string text) =>
        [.. WordPattern().Matches(text.ToLowerInvariant()).Select(m => m.Value).Where(w => w.Length >= 3)];

    [GeneratedRegex(@"\p{L}[\p{L}\p{N}]*|\p{N}+")]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"(^|\n)0:00 \S")]
    private static partial Regex ChapterLine();
}
