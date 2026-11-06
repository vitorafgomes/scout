using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.SemanticKernel;
using Scout.Llm;
using Scout.Llm.Abstractions;
using Scout.Plugin.GitHub.Abstractions;

namespace Scout.Plugin.GitHub;

/// <summary>
/// DI composition for the GitHub plugin. Binds <see cref="GitHubOptions"/> from "Scout:Plugins:GitHub". The plugin
/// self-gates: when neither a token nor a token file is configured the call is a clean no-op (no client, no plugin),
/// so an unconfigured host still starts. Otherwise it registers a typed <see cref="IGitHubClient"/> and
/// <see cref="IGitHubWriteClient"/> over one configured <see cref="HttpClient"/> shape, the <c>GitHub</c> read plugin,
/// the <c>GitHubActions</c> plugin whose two functions are declared writes into Scout's data folder, and the executor
/// that posts what the owner approved.
/// </summary>
public static class GitHubServiceCollectionExtensions
{
    /// <summary>Configuration section bound to <see cref="GitHubOptions"/>.</summary>
    public const string ConfigSection = "Scout:Plugins:GitHub";

    private const string ApiBaseAddress = "https://api.github.com/";

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
        var writes = services.AddHttpClient<IGitHubWriteClient, GitHubWriteClient>(Configure);
        if (useTokenFile)
        {
            reads.AddHttpMessageHandler(() => new GitHubTokenFileHandler(tokenFile!));
            writes.AddHttpMessageHandler(() => new GitHubTokenFileHandler(tokenFile!));
        }

        services.AddSingleton(sp => KernelPluginFactory.CreateFromObject(
            new GitHubPlugin(sp.GetRequiredService<IGitHubClient>()), "GitHub"));

        var draftsDirectory = section.GetValue<string>("DraftsDirectory");
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(new GitHubDraftStore(string.IsNullOrWhiteSpace(draftsDirectory) ? DefaultDraftsDirectory() : draftsDirectory));
        var profilesDirectory = section.GetValue<string>("ProfilesDirectory");
        services.AddSingleton(new GitHubProfileStore(string.IsNullOrWhiteSpace(profilesDirectory) ? DefaultProfilesDirectory() : profilesDirectory));
        services.AddSingleton(sp => KernelPluginFactory.CreateFromObject(
            new GitHubActionsPlugin(
                sp.GetRequiredService<IGitHubClient>(),
                sp.GetRequiredService<IWriteGate>(),
                sp.GetRequiredService<GitHubDraftStore>(),
                sp.GetRequiredService<GitHubProfileStore>(),
                sp.GetRequiredService<TimeProvider>()),
            GitHubActionsPlugin.PluginName));
        services.AddWriteFunction(GitHubActionsPlugin.PluginName, GitHubActionsPlugin.SubmitReview, GitHubActionsPlugin.FeatureId,
            GitHubActionsPlugin.DataFolderScope, summaryArguments: ["owner", "repo", "number"]);
        services.AddWriteFunction(GitHubActionsPlugin.PluginName, GitHubActionsPlugin.CreateIssue, GitHubActionsPlugin.FeatureId,
            GitHubActionsPlugin.DataFolderScope, summaryArguments: ["owner", "repo", "title"]);
        services.AddWriteFunction(GitHubActionsPlugin.PluginName, GitHubActionsPlugin.RememberConventions, GitHubActionsPlugin.FeatureId,
            GitHubActionsPlugin.DataFolderScope, summaryArguments: ["owner", "repo"]);
        services.AddHostedService<GitHubActionExecutor>();

        return services;
    }

    /// <summary>Scout's data folder for the drafts: <c>Scout/github-drafts</c> under the user's local application data.</summary>
    internal static string DefaultDraftsDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scout", "github-drafts");

    /// <summary>Scout's data folder for the conventions profiles: <c>Scout/github-profiles</c> under the user's local application data.</summary>
    internal static string DefaultProfilesDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scout", "github-profiles");
}
