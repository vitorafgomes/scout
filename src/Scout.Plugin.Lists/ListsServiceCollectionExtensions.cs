using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Plugin.Common;
using Scout.Plugin.Common.Abstractions;

namespace Scout.Plugin.Lists;

/// <summary>
/// DI composition for the Lists plugin. Binds <see cref="ListsOptions"/> from
/// "Scout:Plugins:Lists" and self-gates on the Enabled flag (default true): when
/// disabled the call is a clean no-op (no kernel plugin), so a host can turn the
/// feature off. When enabled it wires the shared Scout-local store and registers
/// a <see cref="KernelPlugin"/> the LLM can invoke via function-calling.
/// </summary>
public static class ListsServiceCollectionExtensions
{
    /// <summary>Configuration section bound to <see cref="ListsOptions"/>.</summary>
    public const string ConfigSection = "Scout:Plugins:Lists";

    /// <summary>
    /// Register the Lists plugin. No-op when the section sets Enabled to false.
    /// </summary>
    public static IServiceCollection AddListsPlugin(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigSection);
        services.Configure<ListsOptions>(section);

        var enabled = section.GetValue<bool?>("Enabled") ?? true;
        if (!enabled)
        {
            return services;
        }

        services.AddScoutPluginCommon(configuration);

        services.AddSingleton(sp => KernelPluginFactory.CreateFromObject(
            new ListsPlugin(sp.GetRequiredService<IScoutLocalStore>()), "Lists"));

        return services;
    }
}
