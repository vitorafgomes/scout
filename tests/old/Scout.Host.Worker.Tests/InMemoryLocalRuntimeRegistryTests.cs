using NSubstitute;
using Scout.Host.Worker.Providers;
using Shouldly;

namespace Scout.Host.Worker.Tests;

public sealed class InMemoryLocalRuntimeRegistryTests
{
    [Fact]
    public void Track_ReturnsOwned()
    {
        var registry = new InMemoryLocalRuntimeRegistry();
        var process = Substitute.For<ILaunchedProcess>();

        registry.Track("ollama", process);

        registry.GetOwned("ollama").ShouldBeSameAs(process);
    }

    [Fact]
    public void GetOwned_NotTracked_ReturnsNull()
    {
        var registry = new InMemoryLocalRuntimeRegistry();

        registry.GetOwned("ollama").ShouldBeNull();
    }

    [Fact]
    public void Untrack_RemovesEntry()
    {
        var registry = new InMemoryLocalRuntimeRegistry();
        registry.Track("ollama", Substitute.For<ILaunchedProcess>());

        registry.Untrack("ollama");

        registry.GetOwned("ollama").ShouldBeNull();
    }

    [Fact]
    public async Task Track_Concurrent_AllUnique()
    {
        var registry = new InMemoryLocalRuntimeRegistry();
        const int count = 20;
        var processes = Enumerable.Range(0, count)
            .Select(_ => Substitute.For<ILaunchedProcess>())
            .ToArray();

        var tasks = Enumerable.Range(0, count)
            .Select(i => Task.Run(() => registry.Track($"runtime-{i}", processes[i])))
            .ToArray();

        await Task.WhenAll(tasks);

        for (var i = 0; i < count; i++)
        {
            registry.GetOwned($"runtime-{i}").ShouldBeSameAs(processes[i]);
        }
    }
}
