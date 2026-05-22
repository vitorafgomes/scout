using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scout.Host.Worker.Providers;
using Shouldly;

namespace Scout.Host.Worker.Tests;

public sealed class JsonFileLocalRuntimeConfigStoreTests
{
    private static (JsonFileLocalRuntimeConfigStore Store, string Path) NewStore()
    {
        // Path.GetTempFileName creates the file - delete it so the store
        // can observe the "missing file" path on first read.
        var tempFile = Path.GetTempFileName();
        File.Delete(tempFile);

        var options = Options.Create(new LocalRuntimeConfigStoreOptions { FilePath = tempFile });
        var store = new JsonFileLocalRuntimeConfigStore(options, NullLogger<JsonFileLocalRuntimeConfigStore>.Instance);
        return (store, tempFile);
    }

    [Fact]
    public async Task GetAutostartAsync_FreshFile_DefaultsToTrue()
    {
        var (store, path) = NewStore();
        try
        {
            var value = await store.GetAutostartAsync("ollama", CancellationToken.None);
            value.ShouldBeTrue();
        }
        finally
        {
            store.Dispose();
            SafeDelete(path);
        }
    }

    [Fact]
    public async Task SetAutostartAsync_PersistsAcrossReload()
    {
        var (store, path) = NewStore();
        try
        {
            await store.SetAutostartAsync("ollama", enabled: false, CancellationToken.None);
            store.Dispose();

            var reload = new JsonFileLocalRuntimeConfigStore(
                Options.Create(new LocalRuntimeConfigStoreOptions { FilePath = path }),
                NullLogger<JsonFileLocalRuntimeConfigStore>.Instance);
            try
            {
                var value = await reload.GetAutostartAsync("ollama", CancellationToken.None);
                value.ShouldBeFalse();
                (await reload.GetAutostartAsync("lm-studio", CancellationToken.None)).ShouldBeTrue();
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
    public async Task ConcurrentSetAutostart_DoesNotCorruptFile()
    {
        var (store, path) = NewStore();
        try
        {
            var tasks = Enumerable.Range(0, 10)
                .Select(i => Task.Run(() => store.SetAutostartAsync(
                    $"runtime-{i % 3}",
                    enabled: i % 2 == 0,
                    CancellationToken.None)))
                .ToArray();

            await Task.WhenAll(tasks);

            // File must be a parseable JSON object after the storm.
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
    public async Task MalformedFile_DoesNotThrow_FallsBackToDefault()
    {
        var tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFile, "{ not valid json");

        var store = new JsonFileLocalRuntimeConfigStore(
            Options.Create(new LocalRuntimeConfigStoreOptions { FilePath = tempFile }),
            NullLogger<JsonFileLocalRuntimeConfigStore>.Instance);
        try
        {
            var value = await store.GetAutostartAsync("ollama", CancellationToken.None);
            value.ShouldBeTrue();
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
