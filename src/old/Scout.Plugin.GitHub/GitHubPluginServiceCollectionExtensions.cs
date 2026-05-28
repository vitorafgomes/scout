using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scout.Core.Features;
using Scout.Plugin.GitHub.Functions;
using Scout.Plugin.GitHub.Internal;

namespace Scout.Plugin.GitHub;

/// <summary>
/// Composition root for the GitHub feature plugin. Mirrors the Phase 1
/// expectation: one line at the call site
/// (<c>services.AddGitHubFeaturePlugin(configuration)</c>) and the
/// plugin is fully wired, including credential validator and the
/// feature-registrar registration.
/// </summary>
public static class GitHubPluginServiceCollectionExtensions
{
    /// <summary>
    /// Register the GitHub plugin's options, internal services, the
    /// credential validator and the plugin factory marker used by the
    /// feature registrar.
    /// </summary>
    public static IServiceCollection AddGitHubFeaturePlugin(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<GitHubPluginOptions>()
            .Bind(configuration.GetSection(GitHubPluginOptions.SectionName));

        services.AddSingleton<IGitHubClientFactory, GitHubClientFactory>();
        services.AddSingleton<GitHubFunctions>();
        services.AddSingleton<IFeatureCredentialValidator, GitHubCredentialValidator>();
        services.AddFeaturePlugin<GitHubPlugin>(GitHubPlugin.FeatureIdValue);

        return services;
    }
}
