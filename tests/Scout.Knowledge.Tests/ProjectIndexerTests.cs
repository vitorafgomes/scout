using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Scout.Knowledge.Tests;

public sealed class ProjectIndexerTests : IDisposable
{
    private const int Dimensions = 768;

    private readonly string _repoPath = Path.Combine(Path.GetTempPath(), $"scout-indexer-{Guid.NewGuid():N}");

    public ProjectIndexerTests()
    {
        Directory.CreateDirectory(_repoPath);
        File.WriteAllText(
            Path.Combine(_repoPath, "Foo.cs"),
            "public class Foo {\n  public int Bar() { return 1; }\n}\n");
        File.WriteAllText(
            Path.Combine(_repoPath, "README.md"),
            "# Title\n\nSome documentation text.\n");

        var binDir = Path.Combine(_repoPath, "bin");
        Directory.CreateDirectory(binDir);
        File.WriteAllText(Path.Combine(binDir, "skip.cs"), "public class Skipped { }\n");
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

    private static IProjectKnowledgeStore CaptureStore(out Func<IReadOnlyList<ChunkRecord>?> captured)
    {
        var store = Substitute.For<IProjectKnowledgeStore>();
        IReadOnlyList<ChunkRecord>? records = null;
        store
            .ReplaceProjectAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<ChunkRecord>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => records = ci.Arg<IReadOnlyList<ChunkRecord>>());
        captured = () => records;
        return store;
    }

    [Fact]
    public async Task IndexAsync_ChunksEmbedsAndReplacesProject()
    {
        var store = CaptureStore(out var captured);
        var indexer = new ProjectIndexer(new FileChunker(), StubGenerator(), store, NullLogger<ProjectIndexer>.Instance);

        await indexer.IndexAsync(new ProjectEntry { Name = "proj", RepoPath = _repoPath }, CancellationToken.None);

        await store.Received(1).ReplaceProjectAsync(
            "proj",
            Arg.Any<IReadOnlyList<ChunkRecord>>(),
            Arg.Any<CancellationToken>());

        var records = captured();
        records.ShouldNotBeNull();
        records!.Count.ShouldBeGreaterThan(0);
        records.ShouldAllBe(r => r.Project == "proj");
        records.ShouldAllBe(r => !r.Path.Contains('\\'));
        records.ShouldAllBe(r => !r.Path.Contains("bin/"));
        records.ShouldContain(r => r.Path == "Foo.cs" && r.Kind == "code");
        records.ShouldContain(r => r.Path == "README.md" && r.Kind == "doc");
        records.ShouldAllBe(r => r.Embedding.Length == Dimensions);
        records.ShouldAllBe(r => !string.IsNullOrEmpty(r.Content));
        records.ShouldAllBe(r => r.Id.StartsWith("proj:"));
    }

    [Fact]
    public async Task IndexAsync_SkipsFileThatFailsToEmbed_AndIndexesTheRest()
    {
        var store = CaptureStore(out var captured);

        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var inputs = ((IEnumerable<string>)ci[0]).ToList();

                // Fail the file whose context prefix references Foo.cs.
                if (inputs.Any(input => input.Contains("File: Foo.cs", StringComparison.Ordinal)))
                {
                    return Task.FromException<GeneratedEmbeddings<Embedding<float>>>(
                        new InvalidOperationException("embedding failure"));
                }

                return Task.FromResult(Embeddings(inputs.Count));
            });

        var indexer = new ProjectIndexer(new FileChunker(), generator, store, NullLogger<ProjectIndexer>.Instance);

        await Should.NotThrowAsync(() =>
            indexer.IndexAsync(new ProjectEntry { Name = "proj", RepoPath = _repoPath }, CancellationToken.None));

        await store.Received(1).ReplaceProjectAsync(
            "proj",
            Arg.Any<IReadOnlyList<ChunkRecord>>(),
            Arg.Any<CancellationToken>());

        var records = captured();
        records.ShouldNotBeNull();
        records!.ShouldContain(r => r.Path == "README.md");
        records.ShouldNotContain(r => r.Path == "Foo.cs");
    }

    public void Dispose()
    {
        if (Directory.Exists(_repoPath))
        {
            Directory.Delete(_repoPath, recursive: true);
        }
    }
}
