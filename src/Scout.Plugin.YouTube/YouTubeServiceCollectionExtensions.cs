using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Plugin.YouTube.Abstractions;

namespace Scout.Plugin.YouTube;

/// <summary>
/// DI composition for the read-only YouTube plugin. Binds <see cref="YouTubeOptions"/> from
/// "Scout:Plugins:YouTube". The plugin self-gates: when neither an API key nor a token file is
/// configured the call is a clean no-op (no client, no plugin), so an unconfigured host still
/// starts. Otherwise it registers a typed <see cref="IYouTubeClient"/> over a configured
/// <see cref="HttpClient"/> and a <see cref="KernelPlugin"/> the LLM can invoke via Semantic
/// Kernel function-calling. The client logs no requests: the API key travels in the query string,
/// and the framework's URL redaction can be switched off.
/// </summary>
public static class YouTubeServiceCollectionExtensions
{
    /// <summary>Configuration section bound to <see cref="YouTubeOptions"/>.</summary>
    public const string ConfigSection = "Scout:Plugins:YouTube";

    private const string ApiBaseAddress = "https://www.googleapis.com/youtube/v3/";

    /// <summary>
    /// Register the YouTube plugin. No-op when both the API key and the token file are empty
    /// or whitespace; a token file takes precedence over an API key.
    /// </summary>
    public static IServiceCollection AddYouTubePlugin(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigSection);
        services.Configure<YouTubeOptions>(section);

        var apiKey = section.GetValue<string>(nameof(YouTubeOptions.ApiKey));
        var tokenFile = section.GetValue<string>(nameof(YouTubeOptions.TokenFile));
        var useTokenFile = !string.IsNullOrWhiteSpace(tokenFile);
        if (!useTokenFile && string.IsNullOrWhiteSpace(apiKey))
        {
            return services;
        }

        var builder = services.AddHttpClient<IYouTubeClient, YouTubeClient>(client =>
        {
            client.BaseAddress = new Uri(ApiBaseAddress);
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
        });

        builder.AddHttpMessageHandler(useTokenFile
            ? () => new YouTubeTokenFileHandler(tokenFile!)
            : () => new YouTubeApiKeyHandler(apiKey!))
            .RemoveAllLoggers();

        services.AddSingleton(sp => KernelPluginFactory.CreateFromObject(
            new YouTubePlugin(sp.GetRequiredService<IYouTubeClient>()), "YouTube"));

        return services;
    }
}
