using Microsoft.Extensions.Options;
using Scout.Plugin.Common;
using Shouldly;
using Xunit;

namespace Scout.Plugin.Common.Tests;

public sealed class JsonFileScoutLocalStoreTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "scout-store-tests", Guid.NewGuid().ToString("N"));

    private JsonFileScoutLocalStore CreateStore() =>
        new(Options.Create(new ScoutLocalStoreOptions { RootDirectory = _dir }));

    private sealed class Bag
    {
        public List<string> Items { get; set; } = [];
    }

    [Fact]
    public async Task ReadOrCreate_ReturnsFreshDocument_WhenCollectionMissing()
    {
        var store = CreateStore();

        var bag = await store.ReadOrCreateAsync<Bag>("lists");

        bag.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Mutate_PersistsAcrossInstances()
    {
        var store = CreateStore();
        await store.MutateAsync<Bag>("lists", b => b.Items.Add("milk"));

        // A fresh store instance reads the same file back.
        var reopened = CreateStore();
        var bag = await reopened.ReadOrCreateAsync<Bag>("lists");

        bag.Items.ShouldBe(["milk"]);
    }

    [Fact]
    public async Task Mutate_IsAtomicUnderConcurrency()
    {
        var store = CreateStore();

        await Task.WhenAll(Enumerable.Range(0, 50).Select(i =>
            store.MutateAsync<Bag>("lists", b => b.Items.Add($"item-{i}"))));

        var bag = await store.ReadOrCreateAsync<Bag>("lists");
        bag.Items.Count.ShouldBe(50);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("bad/name")]
    [InlineData("bad.name")]
    public async Task InvalidCollectionName_Throws(string collection)
    {
        var store = CreateStore();

        await Should.ThrowAsync<ArgumentException>(
            async () => await store.ReadOrCreateAsync<Bag>(collection));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
