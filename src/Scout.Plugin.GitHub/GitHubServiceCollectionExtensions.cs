using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Plugin.GitHub.Abstractions;

namespace Scout.Plugin.GitHub;

/// <summary>
/// DI composition for the read-only GitHub plugin. Binds
/// <see cref="GitHubOptions"/> from "Scout:Plugins:GitHub". The plugin
/// self-gates: when no token is configured the call is a clean no-op (no client,
/// no plugin), so an unconfigured host still starts. When a token is present it
/// registers a typed <see cref="IGitHubClient"/> over a configured
/// <see cref="HttpClient"/> and a <see cref="KernelPlugin"/> the LLM can invoke
/// via Semantic Kernel function-calling.
/// </summary>
public static class GitHubServiceCollectionExtensions
{
    /// <summary>Configuration section bound to <see cref="GitHubOptions"/>.</summary>
    public const string ConfigSection = "Scout:Plugins:GitHub";

    private const string ApiBaseAddress = "https://api.github.com/";

    /// <summary>
    /// Register the GitHub plugin. No-op when the token is empty or whitespace.
    /// </summary>
    public static IServiceCollection AddGitHubPlugin(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<GitHubOptions>(configuration.GetSection(ConfigSection));

        var token = configuration.GetSection(ConfigSection).GetValue<string>("Token");
        if (string.IsNullOrWhiteSpace(token))
        {
            return services;
        }

        services.AddHttpClient<IGitHubClient, GitHubClient>(client =>
        {
            client.BaseAddress = new Uri(ApiBaseAddress);
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            // GitHub requires a User-Agent on every request.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
        });

        services.AddSingleton(sp => KernelPluginFactory.CreateFromObject(
            new GitHubPlugin(sp.GetRequiredService<IGitHubClient>()), "GitHub"));

        return services;
    }
}
