using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Plugin.Nutrition.Abstractions;

namespace Scout.Plugin.Nutrition;

/// <summary>
/// DI composition for the read-only Nutrition plugin. Binds
/// <see cref="NutritionOptions"/> from "Scout:Plugins:Nutrition". The plugin
/// self-gates on the <c>Enabled</c> flag (default true): when disabled the call
/// is a clean no-op (no client, no plugin), so an unconfigured host still starts.
/// When enabled it registers a typed <see cref="INutritionClient"/> over a
/// configured <see cref="HttpClient"/> and a <see cref="KernelPlugin"/> the LLM
/// can invoke via Semantic Kernel function-calling.
/// </summary>
public static class NutritionServiceCollectionExtensions
{
    /// <summary>Configuration section bound to <see cref="NutritionOptions"/>.</summary>
    public const string ConfigSection = "Scout:Plugins:Nutrition";

    private const string ApiBaseAddress = "https://world.openfoodfacts.org/";

    /// <summary>
    /// Register the Nutrition plugin. No-op when <c>Enabled</c> is false.
    /// </summary>
    public static IServiceCollection AddNutritionPlugin(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigSection);
        services.Configure<NutritionOptions>(section);

        var enabled = section.GetValue("Enabled", true);
        if (!enabled)
        {
            return services;
        }

        services.AddHttpClient<INutritionClient, NutritionClient>(client =>
        {
            client.BaseAddress = new Uri(ApiBaseAddress);
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            // Open Food Facts requires a descriptive User-Agent on every request.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
        });

        services.AddSingleton(sp => KernelPluginFactory.CreateFromObject(
            new NutritionPlugin(sp.GetRequiredService<INutritionClient>()), "Nutrition"));

        return services;
    }
}
