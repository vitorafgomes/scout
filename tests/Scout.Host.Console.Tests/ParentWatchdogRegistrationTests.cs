using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Scout.Host.Console.Tests;

/// <summary>
/// Gating tests for the watchdog registration: the hosted service and probe are
/// present only when SCOUT__PARENT_WATCHDOG=1, and absent otherwise. Asserts on
/// the descriptors so it does not depend on IHostApplicationLifetime (supplied
/// by the real host, not by a bare ServiceCollection).
/// </summary>
public sealed class ParentWatchdogRegistrationTests
{
    private static IServiceCollection BuildServices(string? flag)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddParentWatchdog(flag);
        return services;
    }

    [Fact]
    public void FlagSet_Registers_Probe_And_HostedService()
    {
        var services = BuildServices("1");

        services.ShouldContain(d =>
            d.ServiceType == typeof(IParentLivenessProbe)
            && d.ImplementationType == typeof(StdinEofParentLivenessProbe));
        services.ShouldContain(d =>
            d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(ParentWatchdogService));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("true")]
    public void FlagUnsetOrOther_Registers_Nothing(string? flag)
    {
        var services = BuildServices(flag);

        services.ShouldNotContain(d => d.ServiceType == typeof(IParentLivenessProbe));
        services.ShouldNotContain(d =>
            d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(ParentWatchdogService));
    }
}
