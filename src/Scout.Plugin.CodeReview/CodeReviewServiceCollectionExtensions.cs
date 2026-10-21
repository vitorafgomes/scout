using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Plugin.CodeReview.Abstractions;

namespace Scout.Plugin.CodeReview;

/// <summary>
/// DI composition for the Code Review plugin. Binds <see cref="CodeReviewOptions"/>
/// from "Scout:Plugins:CodeReview" and always registers the model-backed reviewer
/// and the kernel plugin, so the snippet-review functions work whenever an LLM is
/// configured. Repository analysis is gated on the GitHub feature's token
/// ("Scout:Plugins:GitHub:Token"): the repository fetcher is registered only when
/// that token is present, mirroring the GitHub plugin's HTTP client setup.
/// </summary>
public static class CodeReviewServiceCollectionExtensions
{
    /// <summary>Configuration section bound to <see cref="CodeReviewOptions"/>.</summary>
    public const string ConfigSection = "Scout:Plugins:CodeReview";

    /// <summary>Configuration section holding the GitHub feature settings.</summary>
    public const string GitHubConfigSection = "Scout:Plugins:GitHub";

    private const string ApiBaseAddress = "https://api.github.com/";

    /// <summary>
    /// Register the Code Review plugin. The reviewer and plugin are always
    /// registered; the repository fetcher is added only when a GitHub token is
    /// configured.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddCodeReviewPlugin(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<CodeReviewOptions>(configuration.GetSection(ConfigSection));

        // Resolve the chat service lazily: the shared kernel both builds the chat
        // service and enumerates this plugin, so pulling it eagerly here would
        // re-enter the kernel under construction and deadlock.
        services.AddSingleton<ICodeReviewer>(sp => new CodeReviewer(
            () => sp.GetRequiredService<IChatCompletionService>(),
            sp.GetRequiredService<IOptions<CodeReviewOptions>>()));

        // Read the token the same way GitHubServiceCollectionExtensions does so
        // the gating stays consistent with the GitHub feature it reuses.
        var token = configuration.GetSection(GitHubConfigSection).GetValue<string>("Token");
        if (!string.IsNullOrWhiteSpace(token))
        {
            // The typed client carries the configured HttpClient plus the size
            // caps from options, which DI cannot supply positionally; build it
            // through a factory once the HttpClient is configured.
            services.AddHttpClient<IRepositoryFetcher, GitHubRepositoryFetcher>(client =>
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
            })
            .AddTypedClient<IRepositoryFetcher>((http, sp) =>
            {
                var options = sp.GetRequiredService<IOptions<CodeReviewOptions>>().Value;
                return new GitHubRepositoryFetcher(http, options.MaxFileBytes, options.MaxFiles);
            });
        }

        services.AddSingleton(sp => KernelPluginFactory.CreateFromObject(
            new CodeReviewPlugin(
                sp.GetRequiredService<ICodeReviewer>(),
                sp.GetService<IRepositoryFetcher>(),
                sp.GetRequiredService<IOptions<CodeReviewOptions>>()),
            "CodeReview"));

        return services;
    }
}
