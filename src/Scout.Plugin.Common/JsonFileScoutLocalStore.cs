using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Scout.Plugin.Common.Abstractions;

namespace Scout.Plugin.Common;

/// <summary>
/// <see cref="IScoutLocalStore"/> backed by one JSON file per collection under a
/// local directory. Writes go through a temp-file-then-rename so a crash never
/// leaves a half-written file, and a per-collection lock serialises concurrent
/// mutations of the same collection.
/// </summary>
public sealed class JsonFileScoutLocalStore : IScoutLocalStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _rootDirectory;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public JsonFileScoutLocalStore(IOptions<ScoutLocalStoreOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _rootDirectory = options.Value.RootDirectory ?? DefaultRootDirectory();
    }

    private static string DefaultRootDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "Scout", "plugin-data");
    }

    public async Task<T> ReadOrCreateAsync<T>(string collection, CancellationToken cancellationToken = default)
        where T : new()
    {
        var gate = LockFor(collection);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadAsync<T>(collection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<T> MutateAsync<T>(
        string collection,
        Action<T> mutate,
        CancellationToken cancellationToken = default)
        where T : new()
    {
        ArgumentNullException.ThrowIfNull(mutate);

        var gate = LockFor(collection);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await LoadAsync<T>(collection, cancellationToken).ConfigureAwait(false);
            mutate(document);
            await SaveAsync(collection, document, cancellationToken).ConfigureAwait(false);
            return document;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<T> LoadAsync<T>(string collection, CancellationToken cancellationToken)
        where T : new()
    {
        var path = PathFor(collection);
        if (!File.Exists(path))
        {
            return new T();
        }

        await using var stream = File.OpenRead(path);
        var value = await JsonSerializer
            .DeserializeAsync<T>(stream, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);
        return value ?? new T();
    }

    private async Task SaveAsync<T>(string collection, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_rootDirectory);
        var path = PathFor(collection);
        var tempPath = path + ".tmp";

        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer
                .SerializeAsync(stream, value, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    private SemaphoreSlim LockFor(string collection) =>
        _locks.GetOrAdd(NormaliseCollection(collection), _ => new SemaphoreSlim(1, 1));

    private string PathFor(string collection) =>
        Path.Combine(_rootDirectory, NormaliseCollection(collection) + ".json");

    // Collections are plugin-owned identifiers ("lists", "notes"); reject anything
    // that could escape the store directory.
    private static string NormaliseCollection(string collection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        foreach (var c in collection)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')
            {
                throw new ArgumentException(
                    $"Invalid collection name '{collection}'. Use letters, digits, '-' or '_'.",
                    nameof(collection));
            }
        }

        return collection;
    }
}
