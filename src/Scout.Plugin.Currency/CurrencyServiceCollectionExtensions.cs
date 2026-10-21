using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Plugin.Currency.Abstractions;

namespace Scout.Plugin.Currency;

/// <summary>
/// DI composition for the read-only Currency plugin. Binds
/// <see cref="CurrencyOptions"/> from "Scout:Plugins:Currency". The plugin
/// self-gates on the <c>Enabled</c> flag (default true): when disabled the call
/// is a clean no-op (no client, no plugin). When enabled it registers a typed
/// <see cref="ICurrencyClient"/> over a configured <see cref="HttpClient"/> and a
/// <see cref="KernelPlugin"/> the LLM can invoke via function-calling.
/// </summary>
public static class CurrencyServiceCollectionExtensions
{
    /// <summary>Configuration section bound to <see cref="CurrencyOptions"/>.</summary>
    public const string ConfigSection = "Scout:Plugins:Currency";

    private const string ApiBaseAddress = "https://api.frankfurter.dev/";

    /// <summary>
    /// Register the Currency plugin. No-op when <c>Enabled</c> is false.
    /// </summary>
    public static IServiceCollection AddCurrencyPlugin(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigSection);
        services.Configure<CurrencyOptions>(section);

        var enabled = section.GetValue("Enabled", true);
        if (!enabled)
        {
            return services;
        }

        services.AddHttpClient<ICurrencyClient, CurrencyClient>(client =>
        {
            client.BaseAddress = new Uri(ApiBaseAddress);
            client.Timeout = TimeSpan.FromSeconds(30);
            // Some public APIs reject requests without a User-Agent.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
        });

        services.AddSingleton(sp => KernelPluginFactory.CreateFromObject(
            new CurrencyPlugin(sp.GetRequiredService<ICurrencyClient>()), "Currency"));

        return services;
    }
}
