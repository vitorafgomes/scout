using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Scout.Contracts;

namespace Scout.Plugin.SelfInfo;

/// <summary>
/// DI composition for the read-only SelfInfo plugin. Binds
/// <see cref="SelfInfoOptions"/> from "Scout:Plugins:SelfInfo". The plugin
/// self-gates on <c>Enabled</c> and defaults to <c>false</c>: because the plugin
/// answers questions about the host to any workspace member and Semantic Kernel
/// functions have no per-request identity to authorize against, it must be
/// explicitly opted into. When disabled the call is a clean no-op (no kernel
/// plugin). When enabled it registers a <see cref="KernelPlugin"/> the LLM can
/// invoke via Semantic Kernel function-calling.
/// </summary>
public static class SelfInfoServiceCollectionExtensions
{
    /// <summary>Configuration section bound to <see cref="SelfInfoOptions"/>.</summary>
    public const string ConfigSection = "Scout:Plugins:SelfInfo";

    /// <summary>
    /// Register the SelfInfo plugin. No-op unless <c>Enabled</c> is explicitly
    /// true. The default is <c>false</c>: this plugin is opt-in for security.
    /// </summary>
    public static IServiceCollection AddSelfInfoPlugin(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigSection);
        services.Configure<SelfInfoOptions>(section);

        // Ambient per-request identity the plugin reads at call time to gate output
        // disclosure. TryAdd keeps a single shared instance across the process.
        services.AddScoutRequestContext();

        // Default false: without an explicit opt-in the diagnostics stay off.
        var enabled = section.GetValue("Enabled", false);
        if (!enabled)
        {
            return services;
        }

        services.AddSingleton(sp => KernelPluginFactory.CreateFromObject(
            new SelfInfoPlugin(
                configuration,
                sp.GetService<TimeProvider>() ?? TimeProvider.System,
                sp.GetRequiredService<IScoutRequestContext>(),
                sp.GetRequiredService<IOptions<SelfInfoOptions>>().Value),
            "SelfInfo"));

        return services;
    }
}
