using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Scout.Plugin.YouTube.Abstractions;

namespace Scout.Plugin.YouTube.Tests.Fakes;

/// <summary>
/// Builds the production composition from "Scout:Plugins:YouTube" settings, with the stub as the
/// primary handler under the real credential handler, so requests run the real pipeline.
/// </summary>
internal static class YouTubeComposition
{
    public static ServiceProvider Compose(
        StubHttpMessageHandler stub,
        string? apiKey = null,
        string? tokenFile = null,
        ILoggerProvider? logs = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Plugins:YouTube:ApiKey"] = apiKey,
                ["Scout:Plugins:YouTube:TokenFile"] = tokenFile,
            })
            .Build();
        var services = new ServiceCollection();
        if (logs is not null)
        {
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        }

        services.AddYouTubePlugin(config);
        foreach (var name in new[] { nameof(IYouTubeClient), nameof(IYouTubeWriteClient), nameof(IYouTubeAnalyticsClient), nameof(IYouTubeReportingClient) })
        {
            services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => stub);
        }

        return services.BuildServiceProvider();
    }
}
