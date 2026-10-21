using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Scout.Plugin.Common;
using Scout.Plugin.Common.Abstractions;

namespace Scout.Plugin.Ledger;

/// <summary>
/// DI composition for the store-only Ledger plugin. Binds <see cref="LedgerOptions"/>
/// from "Scout:Plugins:Ledger". The plugin self-gates on <c>Enabled</c> (default
/// true): when disabled the call is a clean no-op and the host still starts. When
/// enabled it registers the shared Scout-local store and a <see cref="KernelPlugin"/>
/// the LLM can invoke via Semantic Kernel function-calling.
/// </summary>
public static class LedgerServiceCollectionExtensions
{
    /// <summary>Configuration section bound to <see cref="LedgerOptions"/>.</summary>
    public const string ConfigSection = "Scout:Plugins:Ledger";

    /// <summary>
    /// Register the Ledger plugin. No-op when <c>Enabled</c> is false.
    /// </summary>
    public static IServiceCollection AddLedgerPlugin(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigSection);
        services.Configure<LedgerOptions>(section);

        var enabled = section.GetValue("Enabled", true);
        if (!enabled)
        {
            return services;
        }

        services.AddScoutPluginCommon(configuration);

        services.AddSingleton(sp => KernelPluginFactory.CreateFromObject(
            new LedgerPlugin(
                sp.GetRequiredService<IScoutLocalStore>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<IOptions<LedgerOptions>>()),
            "Ledger"));

        return services;
    }
}
