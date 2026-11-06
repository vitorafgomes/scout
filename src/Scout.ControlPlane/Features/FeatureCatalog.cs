namespace Scout.ControlPlane.Features;

/// <summary>Hard-coded catalog of Scout features.</summary>
public sealed class FeatureCatalog : IFeatureCatalog
{
    /// <summary>Canonical id for the GitHub feature.</summary>
    public const string GitHubId = "github";

    /// <summary>Canonical id for the Kubernetes feature.</summary>
    public const string KubernetesId = "kubernetes";

    /// <summary>Canonical id for the YouTube feature.</summary>
    public const string YouTubeId = "youtube";

    /// <summary>Canonical id for the desktop's own screen, microphone and system audio reads.</summary>
    public const string DesktopId = "desktop";

    private static readonly FeatureMetadata GitHub = new(
        Id: GitHubId,
        DisplayName: "GitHub",
        Description: "Query repositories, issues and pull requests from chat, and read a pull request's diffs, comments and files for a code review.",
        RequiresCredentials: true,
        CredentialFields: new[]
        {
            new FeatureCredentialField(
                Key: "pat",
                Kind: "password",
                Label: "Personal Access Token",
                HelperText: "Only for a Scout host without the desktop sign-in. Use a fine-grained token with read-only access to Metadata, Contents, Pull requests and Issues.",
                CreateLinkUrl: null,
                CreateLinkLabel: null),
        },
        SettingsFields: new[]
        {
            new FeatureSettingField(
                Key: "default_repo_url",
                Kind: "text",
                Label: "Default repository URL",
                HelperText: "Used when a question doesn't name a repo (e.g. \"list open PRs\"). Paste the full URL - for example https://github.com/octocat/Hello-World.",
                CreateLinkUrl: null,
                CreateLinkLabel: null,
                Required: true),
        },
        WriteClass: WriteClass.None,
        ScopeMatrix: new[]
        {
            new FeatureScopeRow(
                Scope: "Metadata",
                Actions: new[] { "List the repositories the Scout GitHub App is installed on" },
                Access: ScopeAccess.Read),
            new FeatureScopeRow(
                Scope: "Contents",
                Actions: new[]
                {
                    "Read the README, languages and top-level files of a repository",
                    "Read one file or list one directory of a repository, at a branch, tag or commit, for a code review",
                },
                Access: ScopeAccess.Read),
            new FeatureScopeRow(
                Scope: "Pull requests",
                Actions: new[]
                {
                    "List open pull requests",
                    "Read one pull request with its changed files and their diffs",
                    "Read the comments on a pull request",
                },
                Access: ScopeAccess.Read),
            new FeatureScopeRow(
                Scope: "Issues",
                Actions: new[] { "List open issues", "Read one issue" },
                Access: ScopeAccess.Read),
        },
        SetupSteps: new[]
        {
            "Click 'Connect with GitHub'. Scout shows a one-time code.",
            "Click 'Open GitHub', sign in with your GitHub account and enter the code.",
            "Approve the Scout GitHub App. It asks for read access only; Scout never writes to GitHub.",
            "If the card says so, click 'Install Scout on your GitHub account' and pick the repositories Scout may read.",
        });

    private static readonly FeatureMetadata Kubernetes = new(
        Id: KubernetesId,
        DisplayName: "Kubernetes",
        Description: "Read pods, deployments and events from your clusters. Uses the Kubernetes configuration on this computer, with your consent; read-only.",
        RequiresCredentials: false,
        CredentialFields: Array.Empty<FeatureCredentialField>(),
        SettingsFields: Array.Empty<FeatureSettingField>(),
        WriteClass: WriteClass.None,
        ScopeMatrix: null,
        SetupSteps: new[]
        {
            "Scout looks for the Kubernetes configuration on this computer: the first file named in KUBECONFIG, otherwise ~/.kube/config.",
            "Tick the contexts Scout may use and click 'Use selected contexts'. Nothing is used before you do.",
            "A context whose user runs a command to get a token shows that command before you agree.",
            "Scout only reads: namespaces, pods, deployments and events. It never reads logs or secrets and never changes a cluster.",
            "Your credentials may allow changes, so a read-only user bound to the 'view' cluster role is safer.",
        });

    private static readonly FeatureMetadata YouTube = new(
        Id: YouTubeId,
        DisplayName: "YouTube",
        Description: "Reads a channel's overview, uploads, statistics, search results and comments; your own channel after you sign in from the desktop. With your write permission it also produces videos under Scout's folder and, one action at a time with your approval, uploads, schedules and tests titles and thumbnails. Replying to and moderating comments are not implemented yet.",
        RequiresCredentials: true,
        CredentialFields: new[]
        {
            new FeatureCredentialField(
                Key: "api_key",
                Kind: "password",
                Label: "YouTube Data API key",
                HelperText: "Create it in the Google Cloud Console (APIs & Services, Credentials) for a project with the YouTube Data API v3 enabled. It reads public data only.",
                CreateLinkUrl: null,
                CreateLinkLabel: null),
        },
        SettingsFields: Array.Empty<FeatureSettingField>(),
        WriteClass: WriteClass.Executive,
        ScopeMatrix: new[]
        {
            new FeatureScopeRow(
                Scope: "channels.list",
                Actions: new[] { "Read a channel's title, handle, subscribers, total views and video count" },
                Access: ScopeAccess.Read),
            new FeatureScopeRow(
                Scope: "playlistItems.list",
                Actions: new[] { "List the latest uploads of a channel" },
                Access: ScopeAccess.Read),
            new FeatureScopeRow(
                Scope: "videos.list",
                Actions: new[] { "Read a video's title, duration, views, likes and comment count" },
                Access: ScopeAccess.Read),
            new FeatureScopeRow(
                Scope: "search.list",
                Actions: new[] { "Search YouTube for videos on a topic" },
                Access: ScopeAccess.Read),
            new FeatureScopeRow(
                Scope: "commentThreads.list",
                Actions: new[] { "Read the comments on your videos" },
                Access: ScopeAccess.Read),
            new FeatureScopeRow(
                Scope: "youtubeAnalytics reports.query",
                Actions: new[] { "Read a video's views, watch time, retention and subscribers gained" },
                Access: ScopeAccess.Read),
            new FeatureScopeRow(
                Scope: "youtubeReporting reports.list",
                Actions: new[] { "Read the reach reports with a video's thumbnail impressions and click-through rate" },
                Access: ScopeAccess.Read),
            new FeatureScopeRow(
                Scope: "api.openverse.org and the host of the picture it names",
                Actions: new[] { "Search Openverse for a CC0, public domain or CC BY picture for a scene without one, and download the picture it names into the project's media folder" },
                Access: ScopeAccess.Read),
            new FeatureScopeRow(
                Scope: "Scout's data folder",
                Actions: new[] { "Save scripts, narration, images, the scene list, the video and thumbnails, and keep the recording you attach and its transcript" },
                Access: ScopeAccess.Write),
            new FeatureScopeRow(
                Scope: "youtubeReporting jobs.create",
                Actions: new[] { "Create the channel's reach reporting job once, so YouTube prepares the reach reports" },
                Access: ScopeAccess.Write),
            new FeatureScopeRow(
                Scope: "videos.insert",
                Actions: new[] { "Upload a video as public, private or unlisted, or as private with a time YouTube publishes it" },
                Access: ScopeAccess.IrreversibleWrite),
            new FeatureScopeRow(
                Scope: "videos.update",
                Actions: new[] { "Set a video's title, description, tags, publish time and visibility" },
                Access: ScopeAccess.IrreversibleWrite),
            new FeatureScopeRow(
                Scope: "thumbnails.set",
                Actions: new[] { "Set a video's thumbnail" },
                Access: ScopeAccess.IrreversibleWrite),
            new FeatureScopeRow(
                Scope: "playlistItems.insert",
                Actions: new[] { "Add a video to a playlist" },
                Access: ScopeAccess.IrreversibleWrite),
            new FeatureScopeRow(
                Scope: "comments.insert",
                Actions: new[] { "Publish a reply to a comment" },
                Access: ScopeAccess.IrreversibleWrite),
            new FeatureScopeRow(
                Scope: "comments.setModerationStatus",
                Actions: new[] { "Hold, publish or reject a comment" },
                Access: ScopeAccess.IrreversibleWrite),
        },
        SetupSteps: new[]
        {
            "Create a project in the Google Cloud Console.",
            "Enable the YouTube Data API v3 for that project.",
            "Create an API key for public channels, or sign in with Google from the Scout app to read your own channel.",
        });

    private static readonly FeatureMetadata Desktop = new(
        Id: DesktopId,
        DisplayName: "Desktop",
        Description: "What Scout can see and hear on this computer when you ask from the desktop: a screenshot you attach to a question, your microphone while you record or listen, and the system audio while you keep Others on. Nothing is captured unless you click.",
        RequiresCredentials: false,
        CredentialFields: Array.Empty<FeatureCredentialField>(),
        SettingsFields: Array.Empty<FeatureSettingField>(),
        WriteClass: WriteClass.None,
        ScopeMatrix: new[]
        {
            new FeatureScopeRow(
                Scope: "Screen",
                Actions: new[]
                {
                    "Send one screenshot with a question when you attach it",
                    "Read the text on a screenshot with the bundled OCR program",
                },
                Access: ScopeAccess.Read),
            new FeatureScopeRow(
                Scope: "Microphone",
                Actions: new[] { "Record a voice question", "Listen while you speak" },
                Access: ScopeAccess.Read),
            new FeatureScopeRow(
                Scope: "System audio",
                Actions: new[] { "Listen to what this computer plays while you keep Others on" },
                Access: ScopeAccess.Read),
        });

    private static readonly IReadOnlyList<FeatureMetadata> AllFeatures = new[] { GitHub, Kubernetes, YouTube, Desktop };

    private readonly Dictionary<string, FeatureMetadata> _byId = AllFeatures.ToDictionary(
        f => f.Id,
        StringComparer.Ordinal);

    /// <summary>Build the catalog; throws when an entry breaks <see cref="Validate"/>.</summary>
    public FeatureCatalog() => Validate(AllFeatures);

    /// <summary>
    /// Fails when a feature declares a write row under <see cref="WriteClass.None"/>,
    /// because the class is what the owner grants.
    /// </summary>
    /// <exception cref="InvalidOperationException">Names every inconsistent feature.</exception>
    public static void Validate(IEnumerable<FeatureMetadata> features)
    {
        ArgumentNullException.ThrowIfNull(features);
        var inconsistent = features
            .Where(f => f.WriteClass == WriteClass.None && f.HasWriteRows)
            .Select(f => f.Id)
            .ToList();
        if (inconsistent.Count > 0)
        {
            throw new InvalidOperationException(
                $"Features with a write row must declare a write class other than None: {string.Join(", ", inconsistent)}.");
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<FeatureMetadata> All => AllFeatures;

    /// <inheritdoc/>
    public FeatureMetadata? Find(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return _byId.GetValueOrDefault(id);
    }
}
