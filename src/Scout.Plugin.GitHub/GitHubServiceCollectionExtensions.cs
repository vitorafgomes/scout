using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Scout.Llm;
using Scout.Plugin.GitHub.Abstractions;

namespace Scout.Plugin.GitHub;

/// <summary>
/// DI composition for the GitHub plugin. Binds <see cref="GitHubOptions"/> from "Scout:Plugins:GitHub". The plugin
/// self-gates: when neither a token nor a token file is configured the call is a clean no-op (no client, no plugin),
/// so an unconfigured host still starts. Otherwise it registers a typed <see cref="IGitHubClient"/> and
/// <see cref="IGitHubWriteClient"/> over one configured <see cref="HttpClient"/> shape, the <c>GitHub</c> read plugin,
/// and the <c>GitHubActions</c> plugin with its declared writes, each on its row of the <c>github</c> feature: the
/// review, the verdict, the comments on a pull request, the issue and the comments on an issue (all five irreversible), and the
/// conventions profile and the snapshot in Scout's data folder; and the <see cref="GitHubVerdictRunner"/> and <see cref="GitHubWriteRunner"/>
/// a host's executor runs an approved verdict, review, issue, comment or reply with. No hosted service.
/// </summary>
public static class GitHubServiceCollectionExtensions
{
    /// <summary>Configuration section bound to <see cref="GitHubOptions"/>.</summary>
    public const string ConfigSection = "Scout:Plugins:GitHub";

    private const string ApiBaseAddress = "https://api.github.com/";

    /// <summary>The named client the bot's writes go through, its token read from the bot's token file.</summary>
    internal const string BotWritesClient = "GitHubBotWrites";

    /// <summary>
    /// Register the GitHub plugin. No-op when both the token and the token file are empty
    /// or whitespace; a token file takes precedence over a token.
    /// </summary>
    public static IServiceCollection AddGitHubPlugin(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<GitHubOptions>(configuration.GetSection(ConfigSection));

        var section = configuration.GetSection(ConfigSection);
        var token = section.GetValue<string>("Token");
        var tokenFile = section.GetValue<string>("TokenFile");
        var useTokenFile = !string.IsNullOrWhiteSpace(tokenFile);
        if (!useTokenFile && string.IsNullOrWhiteSpace(token))
        {
            return services;
        }

        void Configure(HttpClient client)
        {
            client.BaseAddress = new Uri(ApiBaseAddress);
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            // GitHub requires a User-Agent on every request.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
            if (!useTokenFile)
            {
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);
            }
        }

        var reads = services.AddHttpClient<IGitHubClient, GitHubClient>(Configure);
        var writes = services.AddHttpClient(nameof(IGitHubWriteClient), Configure);
        if (useTokenFile)
        {
            reads.AddHttpMessageHandler(() => new GitHubTokenFileHandler(tokenFile!));
            writes.AddHttpMessageHandler(() => new GitHubTokenFileHandler(tokenFile!));
        }

        var botTokenFile = section.GetValue<string>("BotTokenFile");
        var hasBot = !string.IsNullOrWhiteSpace(botTokenFile);
        if (hasBot)
        {
            services.AddHttpClient(BotWritesClient, client =>
                {
                    client.BaseAddress = new Uri(ApiBaseAddress);
                    client.Timeout = TimeSpan.FromSeconds(30);
                    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                    client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
                })
                .AddHttpMessageHandler(() => new GitHubTokenFileHandler(botTokenFile!));
        }

        var switches = hasBot
            ? BotSwitches.Parse(section.GetValue<string>("BotWrites"), section.GetValue<string>("BotRepos"), section.GetValue<string>("BotLogin"))
            : BotSwitches.None;
        services.AddTransient<IGitHubWriteClient>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new GitHubWriteClient(
                factory.CreateClient(nameof(IGitHubWriteClient)), hasBot ? factory.CreateClient(BotWritesClient) : null, switches);
        });

        var snapshotsDirectory = section.GetValue<string>("SnapshotsDirectory");
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new RepositorySnapshotStore(
            string.IsNullOrWhiteSpace(snapshotsDirectory) ? DefaultSnapshotsDirectory() : snapshotsDirectory,
            section.GetValue<long>("SnapshotMaxBytes"),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IRepositorySnapshots>(sp => sp.GetRequiredService<RepositorySnapshotStore>());
        services.AddSingleton<TestRuns>();
        services.AddSingleton<RunningTests>();
        services.AddSingleton(sp => KernelPluginFactory.CreateFromObject(
            new GitHubPlugin(sp.GetRequiredService<IGitHubClient>(), sp.GetRequiredService<RepositorySnapshotStore>(), switches, sp.GetRequiredService<TestRuns>()), "GitHub"));

        var profilesDirectory = section.GetValue<string>("ProfilesDirectory");
        services.AddSingleton(new GitHubProfileStore(string.IsNullOrWhiteSpace(profilesDirectory) ? DefaultProfilesDirectory() : profilesDirectory));
        services.AddSingleton(sp => new GitHubActionsPlugin(
            sp.GetRequiredService<IGitHubClient>(),
            sp.GetRequiredService<IGitHubWriteClient>(),
            sp.GetRequiredService<GitHubProfileStore>(),
            sp.GetRequiredService<RepositorySnapshotStore>(),
            sp.GetService<IRepositoryProjects>() ?? NoProjects.Instance,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<IRepositoryAudits>(),
            sp.GetService<ISandboxRunner>(),
            sp.GetRequiredService<TestRuns>(),
            sp.GetService<IStackDetector>(),
            sp.GetService<ILogger<GitHubActionsPlugin>>(),
            sp.GetRequiredService<RunningTests>()));
        services.AddSingleton(sp => KernelPluginFactory.CreateFromObject(
            sp.GetRequiredService<GitHubActionsPlugin>(), GitHubActionsPlugin.PluginName));
        services.AddSingleton(sp => new GitHubVerdictRunner(
            sp.GetRequiredService<GitHubActionsPlugin>(), sp.GetRequiredService<IGitHubClient>(), sp.GetRequiredService<IGitHubWriteClient>()));
        services.AddSingleton(sp => new GitHubWriteRunner(
            sp.GetRequiredService<GitHubActionsPlugin>(), sp.GetRequiredService<IGitHubClient>(), sp.GetRequiredService<IGitHubWriteClient>()));
        services.AddWriteFunction(GitHubActionsPlugin.PluginName, GitHubActionsPlugin.SubmitReview, GitHubActionsPlugin.FeatureId,
            GitHubActionsPlugin.ReviewScope, summaryArguments: ["owner", "repo", "number"], carriesPayload: true);
        services.AddWriteFunction(GitHubActionsPlugin.PluginName, GitHubActionsPlugin.CreateIssue, GitHubActionsPlugin.FeatureId,
            GitHubActionsPlugin.IssueScope, summaryArguments: ["owner", "repo", "title"], carriesPayload: true);
        services.AddWriteFunction(GitHubActionsPlugin.PluginName, GitHubActionsPlugin.GiveVerdict, GitHubActionsPlugin.FeatureId,
            GitHubActionsPlugin.VerdictScope, summaryArguments: ["owner", "repo", "number", "verdict"], carriesPayload: true);
        services.AddWriteFunction(GitHubActionsPlugin.PluginName, GitHubActionsPlugin.CommentOnPullRequest, GitHubActionsPlugin.FeatureId,
            GitHubActionsPlugin.PullRequestCommentScope, summaryArguments: ["owner", "repo", "number"], carriesPayload: true);
        services.AddWriteFunction(GitHubActionsPlugin.PluginName, GitHubActionsPlugin.ReplyToReviewComment, GitHubActionsPlugin.FeatureId,
            GitHubActionsPlugin.PullRequestCommentScope, summaryArguments: ["owner", "repo", "number", "comment_id"], carriesPayload: true);
        services.AddWriteFunction(GitHubActionsPlugin.PluginName, GitHubActionsPlugin.CommentOnIssue, GitHubActionsPlugin.FeatureId,
            GitHubActionsPlugin.IssueCommentScope, summaryArguments: ["owner", "repo", "number"], carriesPayload: true);
        services.AddWriteFunction(GitHubActionsPlugin.PluginName, GitHubActionsPlugin.RememberConventions, GitHubActionsPlugin.FeatureId,
            GitHubActionsPlugin.DataFolderScope, summaryArguments: ["owner", "repo"]);
        services.AddWriteFunction(GitHubActionsPlugin.PluginName, GitHubActionsPlugin.SnapshotRepository, GitHubActionsPlugin.FeatureId,
            GitHubActionsPlugin.DataFolderScope, summaryArguments: ["owner", "repo", "reference"]);
        services.AddWriteFunction(GitHubActionsPlugin.PluginName, GitHubActionsPlugin.StartAudit, GitHubActionsPlugin.FeatureId,
            GitHubActionsPlugin.DataFolderScope, summaryArguments: ["owner", "repo"]);
        services.AddWriteFunction(GitHubActionsPlugin.PluginName, GitHubActionsPlugin.StopAudit, GitHubActionsPlugin.FeatureId,
            GitHubActionsPlugin.DataFolderScope, summaryArguments: ["owner", "repo"]);
        services.AddWriteFunction(GitHubActionsPlugin.PluginName, GitHubActionsPlugin.PauseAudit, GitHubActionsPlugin.FeatureId,
            GitHubActionsPlugin.DataFolderScope, summaryArguments: ["owner", "repo"]);
        services.AddWriteFunction(GitHubActionsPlugin.PluginName, GitHubActionsPlugin.RunTests, GitHubActionsPlugin.FeatureId,
            GitHubActionsPlugin.LocalContainerScope, summaryArguments: ["owner", "repo", "number"]);
        return services;
    }

    /// <summary>Scout's data folder for the repository snapshots: <c>Scout/github-snapshots</c> under the user's local application data.</summary>
    internal static string DefaultSnapshotsDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scout", "github-snapshots");

    /// <summary>A host that wires no Knowledge index: a snapshot is still read locally, only not searched semantically.</summary>
    private sealed class NoProjects : IRepositoryProjects
    {
        public static readonly NoProjects Instance = new();

        public void Register(string name, string path)
        {
        }
    }

    /// <summary>Scout's data folder for the conventions profiles: <c>Scout/github-profiles</c> under the user's local application data.</summary>
    internal static string DefaultProfilesDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scout", "github-profiles");
}
