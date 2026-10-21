using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Scout.Knowledge.Tests;

public sealed class ProjectWatchServiceTests : IDisposable
{
    private const int Dimensions = 768;
    private const int Debounce = 50;
    private const int PollTimeoutMs = 5000;

    private readonly string _repoPath = Path.Combine(Path.GetTempPath(), $"scout-watch-{Guid.NewGuid():N}");

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

    private ProjectIndexer Indexer(IEmbeddingGenerator<string, Embedding<float>> generator, IProjectKnowledgeStore store)
        => new(new FileChunker(), generator, store, NullLogger<ProjectIndexer>.Instance);

    private ProjectWatchService Service(
        IProjectKnowledgeStore store,
        IEmbeddingGenerator<string, Embedding<float>>? generator = null,
        string? repoPath = null)
    {
        var options = Options.Create(new ProjectsOptions
        {
            Items = { new ProjectEntry { Name = "proj", RepoPath = repoPath ?? _repoPath } },
            Watch = new WatchOptions { Enabled = true, DebounceMilliseconds = Debounce },
        });

        return new ProjectWatchService(
            Indexer(generator ?? StubGenerator(), store),
            store,
            options,
            NullLogger<ProjectWatchService>.Instance);
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
        using var service = Service(store);

        await StartAndWaitReadyAsync(service);

        var fooPath = Path.Combine(_repoPath, "Foo.cs");
        for (int i = 0; i < 5; i++)
        {
            File.WriteAllText(fooPath, $"public class Foo {{ int x = {i}; }}\n");
        }

        await PollUntilAsync(() => ReceivedReplaceFile(store, "Foo.cs"));

        // Allow one more debounce window to confirm no extra calls accrue.
        await Task.Delay(Debounce * 4);

        store.ReceivedCalls()
            .Count(c =>
                c.GetMethodInfo().Name == nameof(IProjectKnowledgeStore.ReplaceFileAsync) &&
                (string)c.GetArguments()[1]! == "Foo.cs")
            .ShouldBe(1);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task EmbedFailureForOneFile_IsLogged_AndDoesNotStopService()
    {
        // Fault injection: the embedder throws for the first file. The service must
        // log+continue and still re-index a subsequently changed file.
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var inputs = ((IEnumerable<string>)ci[0]).ToList();
                if (inputs.Any(input => input.Contains("File: Boom.cs", StringComparison.Ordinal)))
                {
                    return Task.FromException<GeneratedEmbeddings<Embedding<float>>>(
                        new InvalidOperationException("embedding failure"));
                }

                return Task.FromResult(Embeddings(inputs.Count));
            });

        var store = Substitute.For<IProjectKnowledgeStore>();
        using var service = Service(store, generator);

        await StartAndWaitReadyAsync(service);

        File.WriteAllText(Path.Combine(_repoPath, "Boom.cs"), "public class Boom { }\n");

        await PollUntilAsync(() => ReceivedReplaceFile(store, "Boom.cs"));

        // The failing file still results in a ReplaceFileAsync with empty records
        // (BuildRecordsForFileAsync swallows the embed failure and returns empty),
        // and the service stays alive to handle the next change.
        File.WriteAllText(Path.Combine(_repoPath, "Ok.cs"), "public class Ok { }\n");

        await PollUntilAsync(() => ReceivedReplaceFile(store, "Ok.cs"));

        await store.Received().ReplaceFileAsync(
            "proj",
            "Ok.cs",
            Arg.Is<IReadOnlyList<ChunkRecord>>(r => r.Count > 0),
            Arg.Any<CancellationToken>());

        await service.StopAsync(CancellationToken.None);
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
    }
}
