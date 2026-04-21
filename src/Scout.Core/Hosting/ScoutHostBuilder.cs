using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scout.Core.Llm;
using Scout.Core.Plugins;

namespace Scout.Core.Hosting;

/// <summary>
/// DI composition for the Scout core. Consumer applications call
/// <c>services.AddScout(configuration)</c> to wire the registry, options,
/// and the host itself, and then register their chosen chat adapter, LLM
/// provider, and feature plugins separately. This keeps Scout.Core free of
/// hard references to any specific adapter or provider package.
/// </summary>
public static class ScoutHostBuilder
{
    /// <summary>
    /// Register the Scout core components on <paramref name="services"/>.
    /// Binds <see cref="ScoutOptions"/> from the "Scout" section and
    /// <see cref="LlmOptions"/> from the "Scout:Llm" section.
    /// </summary>
    public static IServiceCollection AddScout(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<ScoutOptions>(configuration.GetSection("Scout"));
        services.Configure<LlmOptions>(configuration.GetSection("Scout:Llm"));

        services.AddSingleton<PluginRegistry>();
        services.AddSingleton<ScoutHost>();

        return services;
    }
}
