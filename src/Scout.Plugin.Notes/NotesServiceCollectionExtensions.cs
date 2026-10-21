using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Plugin.Common;
using Scout.Plugin.Common.Abstractions;

namespace Scout.Plugin.Notes;

/// <summary>
/// DI composition for the store-only Notes plugin. Binds <see cref="NotesOptions"/>
/// from "Scout:Plugins:Notes". The plugin self-gates on <c>Enabled</c> (default
/// true): when disabled the call is a clean no-op. When enabled it registers the
/// shared Scout-local store and a <see cref="KernelPlugin"/> the LLM can invoke
/// via Semantic Kernel function-calling.
/// </summary>
public static class NotesServiceCollectionExtensions
{
    /// <summary>Configuration section bound to <see cref="NotesOptions"/>.</summary>
    public const string ConfigSection = "Scout:Plugins:Notes";

    /// <summary>
    /// Register the Notes plugin. No-op when <c>Enabled</c> is false.
    /// </summary>
    public static IServiceCollection AddNotesPlugin(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigSection);
        services.Configure<NotesOptions>(section);

        var enabled = section.GetValue("Enabled", true);
        if (!enabled)
        {
            return services;
        }

        services.AddScoutPluginCommon(configuration);

        services.AddSingleton(sp => KernelPluginFactory.CreateFromObject(
            new NotesPlugin(
                sp.GetRequiredService<IScoutLocalStore>(),
                sp.GetRequiredService<TimeProvider>()),
            "Notes"));

        return services;
    }
}
