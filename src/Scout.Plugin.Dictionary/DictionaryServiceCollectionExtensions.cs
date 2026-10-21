using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Plugin.Dictionary.Abstractions;

namespace Scout.Plugin.Dictionary;

/// <summary>
/// DI composition for the read-only Dictionary plugin. Binds
/// <see cref="DictionaryOptions"/> from "Scout:Plugins:Dictionary". Both back-ends
/// (dictionaryapi.dev and Datamuse) are keyless, so the plugin self-gates on an
/// <c>Enabled</c> flag (default true): when disabled the call is a clean no-op (no
/// clients, no plugin), so an unconfigured host still starts. When enabled it
/// registers a typed <see cref="IDefinitionsClient"/> and <see cref="IDatamuseClient"/>,
/// each over its own configured <see cref="HttpClient"/>, and a
/// <see cref="KernelPlugin"/> the LLM can invoke via Semantic Kernel
/// function-calling.
/// </summary>
public static class DictionaryServiceCollectionExtensions
{
    /// <summary>Configuration section bound to <see cref="DictionaryOptions"/>.</summary>
    public const string ConfigSection = "Scout:Plugins:Dictionary";

    private const string DefinitionsApiBaseAddress = "https://api.dictionaryapi.dev/";
    private const string DatamuseApiBaseAddress = "https://api.datamuse.com/";

    /// <summary>
    /// Register the Dictionary plugin. No-op when the section sets Enabled to false.
    /// </summary>
    public static IServiceCollection AddDictionaryPlugin(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigSection);
        services.Configure<DictionaryOptions>(section);

        var enabled = section.GetValue<bool?>("Enabled") ?? true;
        if (!enabled)
        {
            return services;
        }

        services.AddHttpClient<IDefinitionsClient, DefinitionsClient>(client =>
        {
            client.BaseAddress = new Uri(DefinitionsApiBaseAddress);
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            // Some public APIs reject requests without a User-Agent.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
        });

        services.AddHttpClient<IDatamuseClient, DatamuseClient>(client =>
        {
            client.BaseAddress = new Uri(DatamuseApiBaseAddress);
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            // Some public APIs reject requests without a User-Agent.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
        });

        services.AddSingleton(sp => KernelPluginFactory.CreateFromObject(
            new DictionaryPlugin(
                sp.GetRequiredService<IDefinitionsClient>(),
                sp.GetRequiredService<IDatamuseClient>()),
            "Dictionary"));

        return services;
    }
}
