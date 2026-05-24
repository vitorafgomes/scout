using Scout.Host.Worker.Features;

namespace Scout.Host.Worker.Tests.Features;

public sealed class InMemoryCredentialStoreTests
{
    [Fact]
    public void IsAvailable_DefaultsToFalse()
    {
        var store = new InMemoryCredentialStore();
        store.IsAvailable.ShouldBeFalse();
    }

    [Fact]
    public void IsAvailable_TestCtor_CanFlipToTrue()
    {
        var store = new InMemoryCredentialStore(isAvailable: true);
        store.IsAvailable.ShouldBeTrue();
    }

    [Fact]
    public async Task StoreThenGet_RoundTrips()
    {
        var store = new InMemoryCredentialStore(isAvailable: true);
        var ok = await store.StoreAsync("k1", "v1", CancellationToken.None);
        ok.ShouldBeTrue();

        var got = await store.GetAsync("k1", CancellationToken.None);
        got.ShouldBe("v1");
    }

    [Fact]
    public async Task Get_Missing_ReturnsNull()
    {
        var store = new InMemoryCredentialStore();
        var got = await store.GetAsync("missing", CancellationToken.None);
        got.ShouldBeNull();
    }

    [Fact]
    public async Task Contains_TracksPresence()
    {
        var store = new InMemoryCredentialStore();
        (await store.ContainsAsync("k1", CancellationToken.None)).ShouldBeFalse();

        await store.StoreAsync("k1", "v1", CancellationToken.None);
        (await store.ContainsAsync("k1", CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task Delete_RemovesEntry()
    {
        var store = new InMemoryCredentialStore();
        await store.StoreAsync("k1", "v1", CancellationToken.None);
        await store.DeleteAsync("k1", CancellationToken.None);

        (await store.ContainsAsync("k1", CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task Delete_Missing_IsNoOp()
    {
        var store = new InMemoryCredentialStore();
        await store.DeleteAsync("never-existed", CancellationToken.None);
        // No assertion needed — completion without exception is the contract.
        true.ShouldBeTrue();
    }

    [Fact]
    public void Store_NullKey_Throws()
    {
        var store = new InMemoryCredentialStore();
        Should.Throw<ArgumentException>(() =>
            store.StoreAsync("", "v", CancellationToken.None));
    }

    [Fact]
    public async Task Operations_HonourCancellation()
    {
        var store = new InMemoryCredentialStore();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            store.StoreAsync("k", "v", cts.Token));
        await Should.ThrowAsync<OperationCanceledException>(() =>
            store.GetAsync("k", cts.Token));
        await Should.ThrowAsync<OperationCanceledException>(() =>
            store.DeleteAsync("k", cts.Token));
        await Should.ThrowAsync<OperationCanceledException>(() =>
            store.ContainsAsync("k", cts.Token));
    }
}
