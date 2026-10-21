using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Scout.Plugin.Common;
using Scout.Plugin.Common.Abstractions;

namespace Scout.Plugin.Reminders;

/// <summary>
/// DI composition for the store-only Reminders plugin. Binds
/// <see cref="RemindersOptions"/> from "Scout:Plugins:Reminders". The plugin
/// self-gates on <c>Enabled</c> (default true): when disabled the call is a clean
/// no-op. When enabled it registers the shared Scout-local store and a
/// <see cref="KernelPlugin"/> the LLM can invoke via Semantic Kernel
/// function-calling.
/// </summary>
public static class RemindersServiceCollectionExtensions
{
    /// <summary>Configuration section bound to <see cref="RemindersOptions"/>.</summary>
    public const string ConfigSection = "Scout:Plugins:Reminders";

    /// <summary>
    /// Register the Reminders plugin. No-op when <c>Enabled</c> is false.
    /// </summary>
    public static IServiceCollection AddRemindersPlugin(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigSection);
        services.Configure<RemindersOptions>(section);

        var enabled = section.GetValue("Enabled", true);
        if (!enabled)
        {
            return services;
        }

        services.AddScoutPluginCommon(configuration);

        services.AddSingleton(sp => KernelPluginFactory.CreateFromObject(
            new RemindersPlugin(
                sp.GetRequiredService<IScoutLocalStore>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<IOptions<RemindersOptions>>().Value),
            "Reminders"));

        return services;
    }
}
