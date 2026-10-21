using Microsoft.Extensions.DependencyInjection;
using Scout.Contracts;

namespace Scout.Contracts.Tests;

/// <summary>
/// Registration must yield exactly one shared singleton. Two instances would
/// silently fail closed because the writer and reader would not share the same
/// AsyncLocal, so both the singleton lifetime and the idempotent TryAdd behaviour
/// are asserted here.
/// </summary>
public sealed class ScoutRequestContextServiceCollectionExtensionsTests
{
    [Fact]
    public void Resolves_AsyncLocal_Implementation()
    {
        var services = new ServiceCollection();
        services.AddScoutRequestContext();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IScoutRequestContext>()
            .ShouldBeOfType<AsyncLocalScoutRequestContext>();
    }

    [Fact]
    public void Resolves_A_Single_Shared_Singleton()
    {
        var services = new ServiceCollection();
        services.AddScoutRequestContext();
        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IScoutRequestContext>();
        var second = provider.GetRequiredService<IScoutRequestContext>();

        first.ShouldBeSameAs(second);
    }

    [Fact]
    public void Repeated_Registration_Keeps_A_Single_Registration()
    {
        var services = new ServiceCollection();
        services.AddScoutRequestContext();
        services.AddScoutRequestContext();

        services.Count(d => d.ServiceType == typeof(IScoutRequestContext)).ShouldBe(1);
    }

    [Fact]
    public void Throws_When_Services_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddScoutRequestContext());
    }
}
