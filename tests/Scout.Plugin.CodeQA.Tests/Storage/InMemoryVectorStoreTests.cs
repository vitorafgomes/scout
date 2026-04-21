using Scout.Plugin.CodeQA.Domain;
using Scout.Plugin.CodeQA.Storage;

namespace Scout.Plugin.CodeQA.Tests.Storage;

public class InMemoryVectorStoreTests
{
    private static CodeChunk Chunk(string id, float[] embedding, string filePath = "f.cs") =>
        new(id, filePath, 1, 10, "content", embedding);

    [Fact]
    public async Task Count_Is_Zero_On_New_Store()
    {
        using var store = new InMemoryVectorStore();

        (await store.CountAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task Upsert_Increments_Count()
    {
        using var store = new InMemoryVectorStore();

        await store.UpsertAsync([Chunk("a", [1f, 0f])], CancellationToken.None);
        await store.UpsertAsync([Chunk("b", [0f, 1f])], CancellationToken.None);

        (await store.CountAsync(CancellationToken.None)).Should().Be(2);
    }

    [Fact]
    public async Task Upsert_Same_Id_Replaces_Entry()
    {
        using var store = new InMemoryVectorStore();

        await store.UpsertAsync([Chunk("a", [1f, 0f], "old.cs")], CancellationToken.None);
        await store.UpsertAsync([Chunk("a", [1f, 0f], "new.cs")], CancellationToken.None);

        var results = await store.SearchAsync(new float[] { 1f, 0f }, 1, CancellationToken.None);
        results.Should().ContainSingle();
        results[0].Chunk.FilePath.Should().Be("new.cs");
    }

    [Fact]
    public async Task Search_Returns_Empty_On_Empty_Store()
    {
        using var store = new InMemoryVectorStore();

        var results = await store.SearchAsync(new float[] { 1f, 0f }, 5, CancellationToken.None);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_Orders_By_Cosine_Similarity_Descending()
    {
        using var store = new InMemoryVectorStore();

        await store.UpsertAsync(
        [
            Chunk("parallel", [1f, 0f]),      // cosine(q) = 1.0
            Chunk("opposite", [-1f, 0f]),     // cosine(q) = -1.0
            Chunk("orthogonal", [0f, 1f]),    // cosine(q) = 0.0
        ],
        CancellationToken.None);

        var results = await store.SearchAsync(new float[] { 1f, 0f }, 3, CancellationToken.None);

        results.Select(r => r.Chunk.Id)
               .Should().Equal("parallel", "orthogonal", "opposite");
        results[0].Score.Should().BeApproximately(1.0, 1e-6);
        results[1].Score.Should().BeApproximately(0.0, 1e-6);
        results[2].Score.Should().BeApproximately(-1.0, 1e-6);
    }

    [Fact]
    public async Task Search_Honors_TopK()
    {
        using var store = new InMemoryVectorStore();

        await store.UpsertAsync(
        [
            Chunk("a", [1f, 0f]),
            Chunk("b", [0.9f, 0.1f]),
            Chunk("c", [0.8f, 0.2f]),
            Chunk("d", [0.7f, 0.3f]),
        ],
        CancellationToken.None);

        var results = await store.SearchAsync(new float[] { 1f, 0f }, 2, CancellationToken.None);

        results.Should().HaveCount(2);
        results.Select(r => r.Chunk.Id).Should().Equal("a", "b");
    }

    [Fact]
    public async Task Search_Normalizes_Differently_Scaled_Vectors_Correctly()
    {
        // Same direction, very different magnitudes — must still score 1.
        using var store = new InMemoryVectorStore();
        await store.UpsertAsync([Chunk("a", [100f, 0f, 0f])], CancellationToken.None);

        var results = await store.SearchAsync(new float[] { 0.01f, 0f, 0f }, 1, CancellationToken.None);

        results[0].Score.Should().BeApproximately(1.0, 1e-6);
    }

    [Fact]
    public async Task Search_Throws_When_Dimensions_Mismatch()
    {
        using var store = new InMemoryVectorStore();
        await store.UpsertAsync([Chunk("a", [1f, 0f, 0f])], CancellationToken.None);

        Func<Task> act = () => store.SearchAsync(new float[] { 1f, 0f }, 1, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
                 .WithMessage("*different dimensions*");
    }

    [Fact]
    public async Task Search_Rejects_NonPositive_TopK()
    {
        using var store = new InMemoryVectorStore();

        Func<Task> zero = () => store.SearchAsync(new float[] { 1f }, 0, CancellationToken.None);
        Func<Task> neg  = () => store.SearchAsync(new float[] { 1f }, -1, CancellationToken.None);

        await zero.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await neg.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Clear_Empties_The_Store()
    {
        using var store = new InMemoryVectorStore();
        await store.UpsertAsync([Chunk("a", [1f, 0f])], CancellationToken.None);

        await store.ClearAsync(CancellationToken.None);

        (await store.CountAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task Upsert_Propagates_Cancellation()
    {
        using var store = new InMemoryVectorStore();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => store.UpsertAsync([Chunk("a", [1f, 0f])], cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Concurrent_Reads_And_Writes_Do_Not_Crash()
    {
        using var store = new InMemoryVectorStore();
        // Seed a few.
        await store.UpsertAsync(
            Enumerable.Range(0, 50)
                      .Select(i => Chunk($"s{i}", [(float)i, 1f]))
                      .ToArray(),
            CancellationToken.None);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(150));

        var writer = Task.Run(async () =>
        {
            var i = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    await store.UpsertAsync(
                        [Chunk($"w{i++}", [(float)i, 2f])], cts.Token);
                }
                catch (OperationCanceledException) { break; }
            }
        }, CancellationToken.None);

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    await store.SearchAsync(new float[] { 1f, 1f }, 10, cts.Token);
                }
                catch (OperationCanceledException) { break; }
            }
        }, CancellationToken.None)).ToArray();

        Func<Task> act = () => Task.WhenAll(readers.Append(writer));
        await act.Should().NotThrowAsync();

        (await store.CountAsync(CancellationToken.None)).Should().BeGreaterThan(50);
    }
}
