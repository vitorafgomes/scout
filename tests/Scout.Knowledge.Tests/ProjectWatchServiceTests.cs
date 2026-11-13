using System.Runtime.Versioning;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Scout.Knowledge.Tests;

public sealed class ProjectWatchServiceTests : IDisposable
{
    private const int Dimensions = 768;
    private const int Debounce = 50;
    private const int PollTimeoutMs = 5000;
    private const string ProjectName = "proj";

    private readonly string _repoPath = Path.Combine(Path.GetTempPath(), $"scout-watch-{Guid.NewGuid():N}");

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"scout-watch-{Guid.NewGuid():N}.db");

    public ProjectWatchServiceTests()
    {
        Directory.CreateDirectory(_repoPath);
    }

    private static GeneratedEmbeddings<Embedding<float>> Embeddings(int count)
    {
        var list = new GeneratedEmbeddings<Embedding<float>>();
        for (int i = 0; i < count; i++)
        {
            list.Add(new Embedding<float>(new float[Dimensions]));
        }

        return list;
    }

    private static IEmbeddingGenerator<string, Embedding<float>> StubGenerator()
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci => Embeddings(((IEnumerable<string>)ci[0]).Count()));
        return generator;
    }

    private static ProjectIndexer Indexer(IEmbeddingGenerator<string, Embedding<float>> generator, IProjectKnowledgeStore store)
        => new(new FileChunker(), generator, store, NullLogger<ProjectIndexer>.Instance);

    private ProjectWatchService Service(
        IProjectKnowledgeStore store,
        IEmbeddingGenerator<string, Embedding<float>>? generator = null,
        string? repoPath = null,
        ILogger<ProjectWatchService>? logger = null,
        TimeProvider? timeProvider = null)
    {
        var options = Options.Create(new ProjectsOptions
        {
            Items = { new ProjectEntry { Name = ProjectName, RepoPath = repoPath ?? _repoPath } },
            Watch = new WatchOptions { Enabled = true, DebounceMilliseconds = Debounce },
        });

        return new ProjectWatchService(
            Indexer(generator ?? StubGenerator(), store),
            store,
            options,
            logger ?? NullLogger<ProjectWatchService>.Instance,
            timeProvider);
    }

    private static async Task PollUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(PollTimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        condition().ShouldBeTrue("condition not met within timeout");
    }

    private static async Task StartAndWaitReadyAsync(ProjectWatchService service)
    {
        await service.StartAsync(CancellationToken.None);
        await PollUntilAsync(() => service.IsWatching);
    }

    private static bool ReceivedReplaceFile(IProjectKnowledgeStore store, string relativePath)
        => store.ReceivedCalls().Any(c =>
            c.GetMethodInfo().Name == nameof(IProjectKnowledgeStore.ReplaceFileAsync) &&
            (string)c.GetArguments()[1]! == relativePath);

    [Fact]
    public async Task ChangedFile_ReindexesThatFile_WithRecords()
    {
        var store = Substitute.For<IProjectKnowledgeStore>();
        using var service = Service(store);

        await StartAndWaitReadyAsync(service);

        File.WriteAllText(Path.Combine(_repoPath, "Foo.cs"), "public class Foo { }\n");

        await PollUntilAsync(() => ReceivedReplaceFile(store, "Foo.cs"));

        await store.Received().ReplaceFileAsync(
            "proj",
            "Foo.cs",
            Arg.Is<IReadOnlyList<ChunkRecord>>(r => r.Count > 0),
            Arg.Any<CancellationToken>());

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Changes_Without_Embeddings_Reindex_The_File_Without_Vectors()
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns<GeneratedEmbeddings<Embedding<float>>>(_ => throw new NotSupportedException("Embeddings are not available."));
        var store = Substitute.For<IProjectKnowledgeStore>();
        var logger = new RecordingLogger();
        using var service = Service(store, generator, logger: logger);
        await StartAndWaitReadyAsync(service);

        File.WriteAllText(Path.Combine(_repoPath, "Foo.cs"), "public class Foo { }\n");
        await PollUntilAsync(() => ReceivedReplaceFile(store, "Foo.cs"));
        await service.StopAsync(CancellationToken.None);

        await store.Received().ReplaceFileAsync(
            "proj",
            "Foo.cs",
            Arg.Is<IReadOnlyList<ChunkRecord>>(r => r.Count > 0 && r.All(c => c.Embedding.Length == 0 && c.Symbol == "Foo")),
            Arg.Any<CancellationToken>());
        logger.Entries.ShouldAllBe(e => e.Exception == null);
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class RecordingLogger : ILogger<ProjectWatchService>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
            }
        }
    }

    [Fact]
    public async Task DeletedFile_ReindexesWithEmptyChunks()
    {
        var fooPath = Path.Combine(_repoPath, "Foo.cs");
        File.WriteAllText(fooPath, "public class Foo { }\n");

        var store = Substitute.For<IProjectKnowledgeStore>();
        using var service = Service(store);

        await StartAndWaitReadyAsync(service);

        File.Delete(fooPath);

        await PollUntilAsync(() => ReceivedReplaceFile(store, "Foo.cs"));

        await store.Received().ReplaceFileAsync(
            "proj",
            "Foo.cs",
            Arg.Is<IReadOnlyList<ChunkRecord>>(r => r.Count == 0),
            Arg.Any<CancellationToken>());

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task IgnoredPath_IsNotReindexed()
    {
        var objDir = Path.Combine(_repoPath, "obj");
        Directory.CreateDirectory(objDir);

        var store = Substitute.For<IProjectKnowledgeStore>();
        using var service = Service(store);

        await StartAndWaitReadyAsync(service);

        File.WriteAllText(Path.Combine(objDir, "Generated.cs"), "public class Gen { }\n");
        // Also touch a real file so we have a positive signal to wait on.
        File.WriteAllText(Path.Combine(_repoPath, "Real.cs"), "public class Real { }\n");

        await PollUntilAsync(() => ReceivedReplaceFile(store, "Real.cs"));

        store.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IProjectKnowledgeStore.ReplaceFileAsync))
            .Select(c => (string)c.GetArguments()[1]!)
            .ShouldNotContain(p => p.Contains("obj/"));

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RapidEdits_AreDebouncedToSingleReindex()
    {
        var store = Substitute.For<IProjectKnowledgeStore>();
        using var clock = new TimerCountingClock();
        var project = new ProjectEntry { Name = ProjectName, RepoPath = _repoPath };
        var fooPath = Path.Combine(_repoPath, "Foo.cs");
        File.WriteAllText(fooPath, "public class Foo { int x; }\n");
        using var service = Service(store, timeProvider: clock);

        await service.StartAsync(CancellationToken.None);
        await clock.NextTimerAsync();

        for (int i = 0; i < 5; i++)
        {
            service.Enqueue(project, fooPath);
        }

        clock.Advance(TimeSpan.FromMilliseconds(Debounce));
        await clock.NextTimerAsync();
        clock.Advance(TimeSpan.FromMilliseconds(Debounce));
        await clock.NextTimerAsync();

        store.ReceivedCalls()
            .Count(c =>
                c.GetMethodInfo().Name == nameof(IProjectKnowledgeStore.ReplaceFileAsync) &&
                (string)c.GetArguments()[1]! == "Foo.cs")
            .ShouldBe(1);

        await service.StopAsync(CancellationToken.None);
    }

    /// <summary>A fake clock that signals each timer armed on it: one per debounce window the service waits out, so a test awaits the moment a window has been drained and the next one is open.</summary>
    private sealed class TimerCountingClock : FakeTimeProvider, IDisposable
    {
        private readonly SemaphoreSlim _armed = new(0);

        public Task NextTimerAsync() => _armed.WaitAsync();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            _armed.Release();
            return timer;
        }

        public void Dispose() => _armed.Dispose();
    }

    private async Task<SqliteProjectKnowledgeStore> StoreHoldingAsync(string relativePath, string symbol)
    {
        var store = new SqliteProjectKnowledgeStore($"Data Source={_dbPath}");
        await store.ReplaceFileAsync(
            ProjectName,
            relativePath,
            [
                new ChunkRecord
                {
                    Id = $"{ProjectName}:{relativePath}:1:0",
                    Project = ProjectName,
                    Path = relativePath,
                    Kind = "code",
                    Symbol = symbol,
                    StartLine = 1,
                    EndLine = 1,
                    Content = $"public class {symbol} {{ }}",
                },
            ],
            CancellationToken.None);
        return store;
    }

    private async Task DrainAsync(ProjectWatchService service, TimerCountingClock clock, string relativePath)
    {
        service.Enqueue(new ProjectEntry { Name = ProjectName, RepoPath = _repoPath }, Path.Combine(_repoPath, relativePath));
        clock.Advance(TimeSpan.FromMilliseconds(Debounce));
        await clock.NextTimerAsync();
    }

    [Fact]
    public async Task A_File_That_Fails_To_Embed_Keeps_The_Rows_It_Had_And_The_Service_Goes_On_To_The_Next_File()
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var inputs = ((IEnumerable<string>)ci[0]).ToList();
                return inputs.Any(input => input.Contains("File: Boom.cs", StringComparison.Ordinal))
                    ? Task.FromException<GeneratedEmbeddings<Embedding<float>>>(new HttpRequestException("Connection refused"))
                    : Task.FromResult(Embeddings(inputs.Count));
            });
        var store = await StoreHoldingAsync("Boom.cs", "BoomBefore");
        File.WriteAllText(Path.Combine(_repoPath, "Boom.cs"), "public class BoomAfter { }\n");
        File.WriteAllText(Path.Combine(_repoPath, "Ok.cs"), "public class Ok { }\n");
        var logger = new RecordingLogger();
        using var clock = new TimerCountingClock();
        using var service = Service(store, generator, logger: logger, timeProvider: clock);
        await service.StartAsync(CancellationToken.None);
        await clock.NextTimerAsync();

        await DrainAsync(service, clock, "Boom.cs");
        await DrainAsync(service, clock, "Ok.cs");
        await service.StopAsync(CancellationToken.None);

        (await store.FindSymbolAsync(ProjectName, "Boom", kind: null, top: 10, CancellationToken.None))
            .ShouldHaveSingleItem().Symbol.ShouldBe("BoomBefore");
        (await store.FindSymbolAsync(ProjectName, "Ok", kind: null, top: 10, CancellationToken.None))
            .ShouldHaveSingleItem().Path.ShouldBe("Ok.cs");
        var warning = logger.Entries.ShouldHaveSingleItem();
        warning.Message.ShouldBe("Failed to re-index Boom.cs in project proj; continuing.");
        warning.Exception.ShouldBeOfType<HttpRequestException>();
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public async Task A_Changed_File_That_Cannot_Be_Read_Keeps_The_Rows_It_Had()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
        {
            return;
        }

        var store = await StoreHoldingAsync("Locked.cs", "LockedBefore");
        var locked = Path.Combine(_repoPath, "Locked.cs");
        File.WriteAllText(locked, "public class LockedAfter { }\n");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        var logger = new RecordingLogger();
        using var clock = new TimerCountingClock();
        using var service = Service(store, logger: logger, timeProvider: clock);
        await service.StartAsync(CancellationToken.None);
        await clock.NextTimerAsync();

        await DrainAsync(service, clock, "Locked.cs");
        await service.StopAsync(CancellationToken.None);

        (await store.FindSymbolAsync(ProjectName, "Locked", kind: null, top: 10, CancellationToken.None))
            .ShouldHaveSingleItem().Symbol.ShouldBe("LockedBefore");
        var warning = logger.Entries.ShouldHaveSingleItem();
        warning.Message.ShouldBe("Failed to re-index Locked.cs in project proj; continuing.");
        warning.Exception.ShouldBeOfType<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task MissingRepoPath_IsSkipped_WithoutThrowing()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"scout-watch-missing-{Guid.NewGuid():N}");
        var store = Substitute.For<IProjectKnowledgeStore>();
        using var service = Service(store, repoPath: missing);

        await Should.NotThrowAsync(() => service.StartAsync(CancellationToken.None));
        await Should.NotThrowAsync(() => service.StopAsync(CancellationToken.None));

        store.ReceivedCalls()
            .Any(c => c.GetMethodInfo().Name == nameof(IProjectKnowledgeStore.ReplaceFileAsync))
            .ShouldBeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_repoPath))
        {
            Directory.Delete(_repoPath, recursive: true);
        }

        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            File.Delete(path);
        }
    }
}
