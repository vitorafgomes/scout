using Scout.Plugin.CodeQA.Indexing;

namespace Scout.Plugin.CodeQA.Tests.Testing;

public class FakeIngestorTests
{
    private static RawFile Sample(string path) =>
        new(path, "sha-" + path, 1, "x");

    [Fact]
    public void Ctor_Null_Files_Throws()
    {
        Action act = () => _ = new FakeIngestor(null!);
        act.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public async Task IngestAsync_Returns_Configured_Snapshot()
    {
        var files = new[] { Sample("a.cs"), Sample("b.cs") };
        var ingestor = new FakeIngestor(files);

        var result = await ingestor.IngestAsync(CancellationToken.None);

        result.Count.ShouldBe(2);
        result[0].Path.ShouldBe("a.cs");
        result[1].Path.ShouldBe("b.cs");
    }

    [Fact]
    public async Task IngestAsync_Empty_Snapshot_Returns_Empty()
    {
        var ingestor = new FakeIngestor(Array.Empty<RawFile>());

        var result = await ingestor.IngestAsync(CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task IngestAsync_Honors_Cancellation()
    {
        var ingestor = new FakeIngestor(new[] { Sample("a.cs") });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => ingestor.IngestAsync(cts.Token));
    }
}
