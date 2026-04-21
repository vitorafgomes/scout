using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scout.Plugin.CloudStatus.Providers;

namespace Scout.Plugin.CloudStatus.Providers.Azure;

/// <summary>
/// DI composition for the Azure cloud health provider. Registers the
/// configuration block under "Scout:Plugins:CloudStatus:Azure", wires
/// <see cref="AzureHealthSource"/> as the default <see cref="IAzureHealthSource"/>,
/// and registers <see cref="AzureCloudHealthProvider"/> as an
/// <see cref="ICloudHealthProvider"/> so it joins the aggregate plugin fan-out.
/// </summary>
public static class AzureServiceCollectionExtensions
{
    public const string ConfigSection = "Scout:Plugins:CloudStatus:Azure";

    public static IServiceCollection AddAzureCloudHealthProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<AzureCloudHealthOptions>(configuration.GetSection(ConfigSection));
        services.AddSingleton<IAzureHealthSource, AzureHealthSource>();
        services.AddSingleton<ICloudHealthProvider, AzureCloudHealthProvider>();

        return services;
    }
}
