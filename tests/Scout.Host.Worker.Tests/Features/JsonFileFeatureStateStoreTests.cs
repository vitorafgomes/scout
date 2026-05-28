using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scout.Host.Worker.Features;

namespace Scout.Host.Worker.Tests.Features;

public sealed class JsonFileFeatureStateStoreTests
{
    private static (JsonFileFeatureStateStore Store, string Path) NewStore()
    {
        var tempFile = Path.GetTempFileName();
        File.Delete(tempFile);

        var options = Options.Create(new FeatureStateStoreOptions { FilePath = tempFile });
        var store = new JsonFileFeatureStateStore(
            options,
            NullLogger<JsonFileFeatureStateStore>.Instance);
        return (store, tempFile);
    }

    [Fact]
    public async Task GetAsync_MissingFile_ReturnsDefault()
    {
        var (store, path) = NewStore();
        try
        {
            var state = await store.GetAsync("github", CancellationToken.None);
            state.Enabled.ShouldBeFalse();
            state.LastError.ShouldBeNull();
        }
        finally
        {
            store.Dispose();
            SafeDelete(path);
        }
    }

    [Fact]
    public async Task SetAsync_RoundTrips_ThroughReload()
    {
        var (store, path) = NewStore();
        try
        {
            await store.SetAsync(
                "github",
                FeatureStates.EnabledWithError("boom"),
                CancellationToken.None);
            store.Dispose();

            var reload = new JsonFileFeatureStateStore(
                Options.Create(new FeatureStateStoreOptions { FilePath = path }),
                NullLogger<JsonFileFeatureStateStore>.Instance);
            try
            {
                var state = await reload.GetAsync("github", CancellationToken.None);
                state.Enabled.ShouldBeTrue();
                state.LastError.ShouldBe("boom");

                var other = await reload.GetAsync("kubernetes", CancellationToken.None);
                other.Enabled.ShouldBeFalse();
            }
            finally
            {
                reload.Dispose();
            }
        }
        finally
        {
            SafeDelete(path);
        }
    }

    [Fact]
    public async Task GetAllAsync_ReturnsSnapshot()
    {
        var (store, path) = NewStore();
        try
        {
            await store.SetAsync("github", FeatureStates.EnabledWithoutSettings(), CancellationToken.None);
            await store.SetAsync("kubernetes", FeatureStates.DisabledWithError("k err"), CancellationToken.None);

            var all = await store.GetAllAsync(CancellationToken.None);
            all.Count.ShouldBe(2);
            all["github"].Enabled.ShouldBeTrue();
            all["kubernetes"].LastError.ShouldBe("k err");
        }
        finally
        {
            store.Dispose();
            SafeDelete(path);
        }
    }

    [Fact]
    public async Task ConcurrentSet_DoesNotCorruptFile()
    {
        var (store, path) = NewStore();
        try
        {
            var tasks = Enumerable.Range(0, 12)
                .Select(i => Task.Run(() => store.SetAsync(
                    $"feature-{i % 3}",
                    new FeatureState(i % 2 == 0, null, FeatureStates.EmptySettings()),
                    CancellationToken.None)))
                .ToArray();

            await Task.WhenAll(tasks);

            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream);
            document.RootElement.ValueKind.ShouldBe(JsonValueKind.Object);
        }
        finally
        {
            store.Dispose();
            SafeDelete(path);
        }
    }

    [Fact]
    public async Task MalformedFile_FallsBackToDefault()
    {
        var tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFile, "{ this is not json");

        var store = new JsonFileFeatureStateStore(
            Options.Create(new FeatureStateStoreOptions { FilePath = tempFile }),
            NullLogger<JsonFileFeatureStateStore>.Instance);
        try
        {
            var state = await store.GetAsync("github", CancellationToken.None);
            state.Enabled.ShouldBeFalse();
        }
        finally
        {
            store.Dispose();
            SafeDelete(tempFile);
        }
    }

    private static void SafeDelete(string path)
    {
        try { File.Delete(path); } catch { /* best-effort cleanup */ }
        try { File.Delete(path + ".tmp"); } catch { /* best-effort cleanup */ }
    }
}
