using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Scout.Plugin.Recipes.Abstractions;

namespace Scout.Plugin.Recipes;

/// <summary>
/// DI composition for the read-only Recipes plugin. Binds
/// <see cref="RecipesOptions"/> from "Scout:Plugins:Recipes". The plugin self-gates
/// on <c>Enabled</c> (default true): when disabled the call is a clean no-op (no
/// client, no plugin), so an unconfigured host still starts. When enabled it
/// registers a typed <see cref="IRecipesClient"/> over a configured
/// <see cref="HttpClient"/> pointed at TheMealDB (the free public test key is baked
/// into the base address) and a <see cref="KernelPlugin"/> the LLM can invoke via
/// Semantic Kernel function-calling.
/// </summary>
public static class RecipesServiceCollectionExtensions
{
    /// <summary>Configuration section bound to <see cref="RecipesOptions"/>.</summary>
    public const string ConfigSection = "Scout:Plugins:Recipes";

    // The trailing "1" is TheMealDB free public test key, baked into the base
    // address so every relative request carries it.
    private const string ApiBaseAddress = "https://www.themealdb.com/api/json/v1/1/";

    /// <summary>
    /// Register the Recipes plugin. No-op when the section sets Enabled to false.
    /// </summary>
    public static IServiceCollection AddRecipesPlugin(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigSection);
        services.Configure<RecipesOptions>(section);

        var enabled = section.GetValue<bool?>("Enabled") ?? true;
        if (!enabled)
        {
            return services;
        }

        services.AddHttpClient<IRecipesClient, RecipesClient>(client =>
        {
            client.BaseAddress = new Uri(ApiBaseAddress);
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            // Some public APIs reject requests without a User-Agent.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
        });

        services.AddSingleton(sp => KernelPluginFactory.CreateFromObject(
            new RecipesPlugin(
                sp.GetRequiredService<IRecipesClient>(),
                sp.GetRequiredService<IOptions<RecipesOptions>>().Value),
            "Recipes"));

        return services;
    }
}
