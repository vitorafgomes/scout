using System.Linq;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Scout.Host.Worker.Features;
using Scout.Host.Worker.Providers;
using Scout.Host.Worker.Tests.Helpers;

namespace Scout.Host.Worker.Tests;

/// <summary>
/// Test factory that boots the worker without its long-running background
/// services (Discord adapter etc.) so the HTTP surface can be exercised in
/// isolation. Tests can opt into a non-loopback remote IP by setting
/// <see cref="ForceNonLoopbackRemoteIp"/> before the first request.
/// </summary>
public sealed class ScoutWorkerWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// When true the test server overrides <c>Connection.RemoteIpAddress</c>
    /// to a non-loopback address before the LoopbackOnly middleware runs.
    /// Used to assert the boundary-failure behaviour of the middleware.
    /// </summary>
    public bool ForceNonLoopbackRemoteIp { get; set; }

    /// <summary>
    /// Optional hook applied after the factory has stripped the production
    /// hosted services and IBinaryLocator/IOllamaApiClient registrations.
    /// Tests use this to plug in their own substitutes.
    /// </summary>
    public Action<IServiceCollection>? ConfigureServicesOverride { get; set; }

    /// <summary>
    /// When true the factory also strips <c>ICredentialStore</c> and
    /// <c>IFeatureStateStore</c> before invoking
    /// <see cref="ConfigureServicesOverride"/> so feature tests can swap
    /// in deterministic in-memory backends. Default false to leave the
    /// providers test suite untouched.
    /// </summary>
    public bool SwapFeatureStores { get; set; }

    private readonly Dictionary<string, string?> _configOverrides = new(StringComparer.Ordinal);

    /// <summary>
    /// Adds an in-memory configuration entry that is layered on top of the
    /// production configuration sources. Tests use this to override timeouts
    /// without touching environment variables.
    /// </summary>
    public ScoutWorkerWebApplicationFactory WithConfiguration(string key, string? value)
    {
        _configOverrides[key] = value;
        return this;
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        if (_configOverrides.Count > 0)
        {
            builder.ConfigureAppConfiguration(cfg =>
                cfg.AddInMemoryCollection(_configOverrides));
        }

        builder.ConfigureServices(services =>
        {
            // Strip Scout's own hosted services that would touch the network
            // on start-up (Discord adapter, indexer, ScoutWorker glue, the
            // SCOUT_HTTP announcer). Framework hosted services such as the
            // generic web host must remain - removing them would kill the
            // TestServer pipeline before any request can reach /providers.
            var hosted = services
                .Where(d =>
                    d.ServiceType == typeof(IHostedService)
                    && d.ImplementationType is { Namespace: not null } impl
                    && impl.Namespace.StartsWith("Scout.", StringComparison.Ordinal))
                .ToList();

            foreach (var descriptor in hosted)
            {
                services.Remove(descriptor);
            }

            // TestServer leaves Connection.RemoteIpAddress null, which the
            // LoopbackOnly middleware treats as forbidden. Inject either
            // loopback (default) or a public address (boundary-failure test)
            // before the middleware runs.
            services.AddSingleton<IStartupFilter>(
                new RemoteIpStartupFilter(ForceNonLoopbackRemoteIp
                    ? IPAddress.Parse("203.0.113.10")
                    : IPAddress.Loopback));

            if (ConfigureServicesOverride is not null)
            {
                // Drop the production locator + api client so the test can
                // register substitutes without ambiguity.
                var toRemove = services
                    .Where(d => d.ServiceType == typeof(IBinaryLocator)
                        || d.ServiceType == typeof(IOllamaApiClient)
                        || d.ServiceType == typeof(IProcessLauncher)
                        || d.ServiceType == typeof(ILocalRuntimeRegistry)
                        || d.ServiceType == typeof(ILocalRuntimeConfigStore)
                        || d.ServiceType == typeof(IOllamaReadinessProbe)
                        || d.ServiceType == typeof(OllamaReadinessProbe))
                    .ToList();
                foreach (var descriptor in toRemove)
                {
                    services.Remove(descriptor);
                }

                if (SwapFeatureStores)
                {
                    var featureRemovals = services
                        .Where(d => d.ServiceType == typeof(ICredentialStore)
                            || d.ServiceType == typeof(IFeatureStateStore))
                        .ToList();
                    foreach (var descriptor in featureRemovals)
                    {
                        services.Remove(descriptor);
                    }
                }

                ConfigureServicesOverride(services);
            }

            // If the test did not register its own readiness probe, default
            // to a probe that is already Ready so HTTP tests do not hang
            // waiting for the autostart hosted service (which is stripped).
            if (!services.Any(d => d.ServiceType == typeof(IOllamaReadinessProbe)))
            {
                services.AddSingleton<IOllamaReadinessProbe>(FakeReadinessProbes.AlwaysReady());
            }
        });

        return base.CreateHost(builder);
    }
}

internal sealed class RemoteIpStartupFilter : IStartupFilter
{
    private readonly IPAddress _remote;

    public RemoteIpStartupFilter(IPAddress remote)
    {
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        // IStartupFilters wrap the existing pipeline, so this runs before
        // anything Program.cs registered with app.UseMiddleware<...>.
        app.Use(async (ctx, n) =>
        {
            ctx.Connection.RemoteIpAddress = _remote;
            await n().ConfigureAwait(false);
        });
        next(app);
    };
}
