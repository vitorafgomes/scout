using Scout.ControlPlane.Features;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Features;

/// <summary>
/// Contract coverage for <see cref="InMemoryCredentialStore"/>: the default
/// instance must fail closed (no durable backend), while the available
/// instance round-trips secrets for the process lifetime.
/// </summary>
public sealed class InMemoryCredentialStoreTests
{
    [Fact]
    public void Default_ReportsUnavailable()
    {
        new InMemoryCredentialStore().IsAvailable.ShouldBeFalse();
    }

    [Fact]
    public async Task StoreAsync_WhenUnavailable_ReturnsFalseAndDoesNotRetain()
    {
        var store = new InMemoryCredentialStore();

        var stored = await store.StoreAsync("scout/github/pat", "ghp_x", CancellationToken.None);

        stored.ShouldBeFalse();
        (await store.ContainsAsync("scout/github/pat", CancellationToken.None)).ShouldBeFalse();
        (await store.GetAsync("scout/github/pat", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task StoreAsync_WhenAvailable_ReturnsTrueAndRoundTrips()
    {
        var store = new InMemoryCredentialStore(isAvailable: true);

        var stored = await store.StoreAsync("scout/github/pat", "ghp_x", CancellationToken.None);

        stored.ShouldBeTrue();
        (await store.ContainsAsync("scout/github/pat", CancellationToken.None)).ShouldBeTrue();
        (await store.GetAsync("scout/github/pat", CancellationToken.None)).ShouldBe("ghp_x");
    }
}
