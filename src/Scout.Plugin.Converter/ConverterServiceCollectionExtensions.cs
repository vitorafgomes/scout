using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace Scout.Plugin.Converter;

/// <summary>
/// DI composition for the offline Converter plugin. Binds
/// <see cref="ConverterOptions"/> from "Scout:Plugins:Converter". The plugin
/// self-gates on <c>Enabled</c> (default true): when disabled the call is a clean
/// no-op (no kernel plugin), so an unconfigured host still starts. When enabled it
/// registers a <see cref="KernelPlugin"/> the LLM can invoke via Semantic Kernel
/// function-calling. The plugin performs pure computation, so no HTTP client and
/// no shared store are required.
/// </summary>
public static class ConverterServiceCollectionExtensions
{
    /// <summary>Configuration section bound to <see cref="ConverterOptions"/>.</summary>
    public const string ConfigSection = "Scout:Plugins:Converter";

    /// <summary>
    /// Register the Converter plugin. No-op when <c>Enabled</c> is false.
    /// </summary>
    public static IServiceCollection AddConverterPlugin(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigSection);
        services.Configure<ConverterOptions>(section);

        var enabled = section.GetValue("Enabled", true);
        if (!enabled)
        {
            return services;
        }

        services.AddSingleton(sp => KernelPluginFactory.CreateFromObject(
            new ConverterPlugin(
                sp.GetRequiredService<IOptions<ConverterOptions>>().Value),
            "Converter"));

        return services;
    }
}
