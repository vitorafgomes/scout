using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scout.Plugin.CloudStatus.Providers;

namespace Scout.Plugin.CloudStatus.Providers.Oci;

/// <summary>
/// DI composition for the OCI cloud health provider. Binds the configuration
/// block under "Scout:Plugins:CloudStatus:OracleCloud" and registers a
/// typed <see cref="System.Net.Http.HttpClient"/> for
/// <see cref="OciHealthSource"/> using <c>IHttpClientFactory</c>.
/// </summary>
public static class OciServiceCollectionExtensions
{
    public const string ConfigSection = "Scout:Plugins:CloudStatus:OracleCloud";

    public static IServiceCollection AddOciCloudHealthProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<OciCloudHealthOptions>(configuration.GetSection(ConfigSection));

        services.AddHttpClient<IOciHealthSource, OciHealthSource>((sp, http) =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OciCloudHealthOptions>>();
            http.Timeout = opts.Value.Timeout;
        });

        services.AddSingleton<ICloudHealthProvider, OciCloudHealthProvider>();

        return services;
    }
}
