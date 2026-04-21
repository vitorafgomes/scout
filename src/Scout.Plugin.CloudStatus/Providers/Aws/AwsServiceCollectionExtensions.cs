using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scout.Plugin.CloudStatus.Providers;

namespace Scout.Plugin.CloudStatus.Providers.Aws;

/// <summary>
/// DI composition for the AWS cloud health provider. Registers the
/// configuration block under "Scout:Plugins:CloudStatus:Aws", wires
/// <see cref="AwsHealthSource"/> as the default <see cref="IAwsHealthSource"/>,
/// and registers <see cref="AwsCloudHealthProvider"/> as an
/// <see cref="ICloudHealthProvider"/> so it joins the aggregate fan-out.
/// </summary>
public static class AwsServiceCollectionExtensions
{
    public const string ConfigSection = "Scout:Plugins:CloudStatus:Aws";

    public static IServiceCollection AddAwsCloudHealthProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<AwsCloudHealthOptions>(configuration.GetSection(ConfigSection));
        services.AddSingleton<IAwsHealthSource, AwsHealthSource>();
        services.AddSingleton<ICloudHealthProvider, AwsCloudHealthProvider>();

        return services;
    }
}
