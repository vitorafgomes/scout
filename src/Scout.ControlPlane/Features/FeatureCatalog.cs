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
        Description: "Query repositories, issues and pull requests from chat; review a pull request and audit a whole project. With your write permission it also posts reviews and comments, opens issues and gives verdicts, each one only after you approve it.",
        RequiresCredentials: true,
        CredentialFields: new[]
        {
            new FeatureCredentialField(
                Key: "pat",
                Kind: "password",
                Label: "Personal Access Token",
                HelperText: "Only for a Scout host without the desktop sign-in. Use a fine-grained token with read access to Metadata, Contents, Checks and Commit statuses, and to Pull requests and Issues; give those two read & write only if you will allow Scout to post reviews, verdicts and comments and open issues.",
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
        WriteClass: WriteClass.Executive,
        ScopeMatrix: new[]
        {
            new FeatureScopeRow(
                Scope: "Metadata",
                Summary: "List the repositories the Scout GitHub App is installed on",
                Actions: new[] { "List the repositories the Scout GitHub App is installed on" },
                Access: ScopeAccess.Read),
            new FeatureScopeRow(
                Scope: "Contents",
                Summary: "Read files, directories and languages; list and search every file to audit a project",
                Actions: new[]
                {
                    "Read the README, languages and top-level files of a repository",
                    "Read one file or list one directory of a repository, at a branch, tag or commit, for a code review",
                    "List every file of a repository and search its code, to audit a whole project",
                },
                Access: ScopeAccess.Read),
            new FeatureScopeRow(
                Scope: "Pull requests",
                Summary: "List and read pull requests, their diffs, comments, checks and reviews, and the account Scout signs in as",
                Actions: new[]
                {
                    "List open pull requests",
                    "Read one pull request with its changed files and their diffs",
                    "Read the comments on a pull request",
                    "Read which account Scout signs in as, so it never approves a pull request that account opened",
                    "Read the check runs and commit statuses of a pull request's head commit, and the reviews already given on it",
                },
                Access: ScopeAccess.Read),
            new FeatureScopeRow(
                Scope: "Issues",
                Summary: "List open issues and read one issue",
                Actions: new[] { "List open issues", "Read one issue" },
                Access: ScopeAccess.Read),
            new FeatureScopeRow(
                Scope: "Notifications",
                Summary: "Search every five minutes for your mentions, review requests and replies; read the pull requests that mention your Scout bot and the bot's own name",
                Actions: new[]
                {
                    "Search your GitHub activity every five minutes for the bell: mentions, review requests and replies on your pull requests and issues",
                    "When your Scout bot is installed, search the open pull requests on its account for a mention of the bot, and read the description, the comments, the review comments and the reviews of each pull request found, to confirm the mention before the bell shows it",
                    "When your Scout bot exists, read its GitHub App's own name and handle at start and whenever the GitHub card reads the bot, so a rename on GitHub is followed",
                },
                Access: ScopeAccess.Read),
            new FeatureScopeRow(
                Scope: "Scout's data folder",
                Summary: "Keep conventions profiles, repository snapshots and audit jobs on this computer",
                Actions: new[]
                {
                    "Keep the conventions profile of a repository Scout audited, so later audits judge its code against its own rules",
                    "Keep a snapshot of a repository at one commit, within a size cap (200 MB unless configured), so a review or an audit reads every file locally and the Knowledge index can search it",
                    "Keep the audit job of a repository, its plan, progress and findings, when you ask for a whole-repository audit in chat; the audit reads the snapshot and pauses or stops when you ask",
                },
                Access: ScopeAccess.Write),
            new FeatureScopeRow(
                Scope: "Pull request reviews",
                Summary: "Post a review with comments; your click on Post on a review card is the approval, and one asked in chat waits for you in Approvals",
                Actions: new[] { "Post a review with comments on a pull request; this review only comments, it never approves or requests changes. A review notifies the pull request's author and everyone following it, so each one needs your approval in Scout Desktop: your click on Post on a review card is that approval, and a review asked for in chat waits for you to approve it" },
                Access: ScopeAccess.IrreversibleWrite),
            new FeatureScopeRow(
                Scope: "Pull request verdicts",
                Summary: "Approve a pull request or request changes; your choice on a review card is the approval, and one asked in chat waits for you in Approvals",
                Actions: new[] { "Approve a pull request or request changes on it when you choose that verdict on the review card; a verdict stays in the pull request's history, so each one runs only after you approve that action in Scout Desktop", "Approve a pull request or request changes on it when you ask for it in chat; each verdict waits for you to approve it in Scout Desktop" },
                Access: ScopeAccess.IrreversibleWrite),
            new FeatureScopeRow(
                Scope: "Pull request comments",
                Summary: "Comment on a pull request or reply to a review comment; each one waits for you in Approvals",
                Actions: new[]
                {
                    "Comment on a pull request's conversation when you ask for it in chat; a comment notifies everyone following the pull request, so each one waits for you to approve that comment in Scout Desktop",
                    "Reply to a review comment on a pull request when you ask for it in chat; each reply waits for you to approve it in Scout Desktop",
                },
                Access: ScopeAccess.IrreversibleWrite),
            new FeatureScopeRow(
                Scope: "New issues",
                Summary: "Open an issue; your click on Open issues on a findings card is the approval, and one asked in chat waits for you in Approvals",
                Actions: new[] { "Open an issue on a repository when you ask for it in chat; an issue notifies the repository's watchers and stays in its history, so each one waits for you to approve that issue in Scout Desktop", "Open the issues a findings card lists when you click Open issues on it; that click is your approval of exactly those issues" },
                Access: ScopeAccess.IrreversibleWrite),
            new FeatureScopeRow(
                Scope: "Issue comments",
                Summary: "Comment on an existing issue; each one waits for you in Approvals",
                Actions: new[] { "Comment on an existing issue when you ask for it in chat; a comment notifies everyone following the issue, so each one waits for you to approve that comment in Scout Desktop" },
                Access: ScopeAccess.IrreversibleWrite),
            new FeatureScopeRow(
                Scope: "Local container",
                Summary: "Run a pull request's tests in a disposable container on this computer",
                Actions: new[]
                {
                    "Run a pull request's own tests in a disposable container on this computer, over the snapshot in Scout's data folder; the container sees no file, secret or variable of yours and, during the tests, no network",
                },
                Access: ScopeAccess.Write),
            new FeatureScopeRow(
                Scope: "Scout bot on GitHub (optional)",
                Summary: "Sign what you switched to the bot with its token, on the repositories you installed it on; you create the bot on GitHub's page and its key stays on this computer",
                Actions: new[]
                {
                    "Sign the reviews, verdicts, comments and issues you switched to the bot with its token, on the repositories you installed it on; on any other repository Scout signs as you and says so, and every read still uses your sign-in",
                    "Creating the bot is your own action in the desktop, like connecting GitHub: you confirm the GitHub App on GitHub's page, and its private key stays in this computer's protected storage",
                },
                Access: ScopeAccess.Write),
        },
        SetupSteps: new[]
        {
            "Click 'Connect with GitHub'. Scout shows a one-time code.",
            "Click 'Open GitHub', sign in with your GitHub account and enter the code.",
            "Approve the Scout GitHub App. It reads your repositories, pull requests and issues; it posts a review or a comment or opens an issue only after you allow the writes here, and only when you ask for it in chat; a verdict also waits for your approval of that action.",
            "If the card says so, click 'Install Scout on your GitHub account' and pick the repositories Scout may use.",
        })
    {
        UnrecordedClaimMayHaveBeenSent = true,
    };

    private static readonly FeatureMetadata Kubernetes = new(
        Id: KubernetesId,
        DisplayName: "Kubernetes",
        Description: "Read pods, deployments and events from your clusters. Uses the Kubernetes configuration on this computer, with your consent; read-only.",
        RequiresCredentials: false,
        CredentialFields: Array.Empty<FeatureCredentialField>(),
        SettingsFields: Array.Empty<FeatureSettingField>(),
        WriteClass: WriteClass.None,
        ScopeMatrix: new[]
        {
            new FeatureScopeRow(
                Scope: "Namespaces",
                Summary: "List the namespaces of each context you chose",
                Actions: new[] { "List the namespaces of each context you chose" },
                Access: ScopeAccess.Read),
            new FeatureScopeRow(
                Scope: "Pods",
                Summary: "Read pod status and restarts; never logs or secrets",
                Actions: new[] { "Read pod status and restarts; never logs or secrets" },
                Access: ScopeAccess.Read),
            new FeatureScopeRow(
                Scope: "Deployments",
                Summary: "Read replicas and rollout status",
                Actions: new[] { "Read replicas and rollout status" },
                Access: ScopeAccess.Read),
            new FeatureScopeRow(
                Scope: "Events",
                Summary: "Read recent events",
                Actions: new[] { "Read recent events" },
                Access: ScopeAccess.Read),
        },
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
        Description: "Reads a channel's overview, uploads, statistics, search results and comments; your own channel after you sign in. With your write permission it also produces videos under Scout's folder and publishes what you approve.",
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
                Summary: "Title, handle, subscribers, total views and video count",
                Actions: new[] { "Read a channel's title, handle, subscribers, total views and video count" },
                Access: ScopeAccess.Read),
            new FeatureScopeRow(
                Scope: "playlistItems.list",
                Summary: "The latest uploads of a channel",
                Actions: new[] { "List the latest uploads of a channel" },
                Access: ScopeAccess.Read),
            new FeatureScopeRow(
                Scope: "videos.list",
                Summary: "Title, duration, views, likes and comment count",
                Actions: new[] { "Read a video's title, duration, views, likes and comment count" },
                Access: ScopeAccess.Read),
            new FeatureScopeRow(
                Scope: "search.list",
                Summary: "Search YouTube for videos on a topic",
                Actions: new[] { "Search YouTube for videos on a topic" },
                Access: ScopeAccess.Read),
            new FeatureScopeRow(
                Scope: "commentThreads.list",
                Summary: "The comments on your videos",
                Actions: new[] { "Read the comments on your videos" },
                Access: ScopeAccess.Read),
            new FeatureScopeRow(
                Scope: "youtubeAnalytics reports.query",
                Summary: "Views, watch time, retention and subscribers gained",
                Actions: new[] { "Read a video's views, watch time, retention and subscribers gained" },
                Access: ScopeAccess.Read),
            new FeatureScopeRow(
                Scope: "youtubeReporting reports.list",
                Summary: "Thumbnail impressions and click-through rate",
                Actions: new[] { "Read the reach reports with a video's thumbnail impressions and click-through rate" },
                Access: ScopeAccess.Read),
            new FeatureScopeRow(
                Scope: "api.openverse.org and the host of the picture it names",
                Summary: "Find a CC0, public domain or CC BY picture for a scene on Openverse and download it",
                Actions: new[] { "Search Openverse for a CC0, public domain or CC BY picture for a scene without one, and download the picture it names into the project's media folder" },
                Access: ScopeAccess.Read),
            new FeatureScopeRow(
                Scope: "Scout's data folder",
                Summary: "Save scripts, narration, images, the scene list, the video and thumbnails, and the recording you attach with its transcript",
                Actions: new[] { "Save scripts, narration, images, the scene list, the video and thumbnails, and keep the recording you attach and its transcript" },
                Access: ScopeAccess.Write),
            new FeatureScopeRow(
                Scope: "youtubeReporting jobs.create",
                Summary: "Create the channel's reach reporting job once",
                Actions: new[] { "Create the channel's reach reporting job once, so YouTube prepares the reach reports" },
                Access: ScopeAccess.Write),
            new FeatureScopeRow(
                Scope: "videos.insert",
                Summary: "Public, private, unlisted or scheduled",
                Actions: new[] { "Upload a video as public, private or unlisted, or as private with a time YouTube publishes it" },
                Access: ScopeAccess.IrreversibleWrite),
            new FeatureScopeRow(
                Scope: "videos.update",
                Summary: "Change a video's title during a title test",
                Actions: new[] { "Change a video's title during a title test: to each title the test tries, and then to the title kept when it ends" },
                Access: ScopeAccess.IrreversibleWrite),
            new FeatureScopeRow(
                Scope: "thumbnails.set",
                Summary: "Set a video's thumbnail",
                Actions: new[] { "Set a video's thumbnail" },
                Access: ScopeAccess.IrreversibleWrite),
            new FeatureScopeRow(
                Scope: "playlistItems.insert",
                Summary: "Add a video to a playlist",
                Actions: new[] { "Add a video to a playlist" },
                Access: ScopeAccess.IrreversibleWrite),
            new FeatureScopeRow(
                Scope: "comments.insert",
                Summary: "Publish a reply to a comment",
                Actions: new[] { "Publish a reply to a comment" },
                Access: ScopeAccess.IrreversibleWrite),
            new FeatureScopeRow(
                Scope: "comments.setModerationStatus",
                Summary: "Hold a comment for your review on YouTube",
                Actions: new[] { "Hold a comment for your review on YouTube; Scout never publishes or rejects a comment" },
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
        Description: "What Scout can see and hear on this computer when you ask from the desktop. Nothing is captured unless you click.",
        RequiresCredentials: false,
        CredentialFields: Array.Empty<FeatureCredentialField>(),
        SettingsFields: Array.Empty<FeatureSettingField>(),
        WriteClass: WriteClass.None,
        ScopeMatrix: new[]
        {
            new FeatureScopeRow(
                Scope: "Screen",
                Summary: "One screenshot you attach to a question, and the text read from it",
                Actions: new[]
                {
                    "Send one screenshot with a question when you attach it",
                    "Read the text on a screenshot with the bundled OCR program",
                },
                Access: ScopeAccess.Read),
            new FeatureScopeRow(
                Scope: "Microphone",
                Summary: "Your voice while you hold to talk, listen or record a meeting",
                Actions: new[] { "Record a voice question", "Listen while you speak", "Listen while you record a meeting" },
                Access: ScopeAccess.Read),
            new FeatureScopeRow(
                Scope: "System audio",
                Summary: "The computer's sound while you keep System audio on or record a meeting",
                Actions: new[]
                {
                    "Listen to what this computer plays while you keep System audio on",
                    "Listen to what this computer plays while you record a meeting",
                },
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
