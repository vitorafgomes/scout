namespace Scout.Plugin.CodeQA.Tests.Embedding;

public class FakeEmbedderTests
{
    [Fact]
    public async Task EmbedAsync_Empty_Input_Returns_Empty_List()
    {
        var embedder = new FakeEmbedder();

        var result = await embedder.EmbedAsync(Array.Empty<string>(), CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task EmbedAsync_Returns_One_Vector_Per_Input_In_Order()
    {
        var embedder = new FakeEmbedder(dimensions: 8);
        string[] inputs = ["alpha", "beta", "gamma"];

        var result = await embedder.EmbedAsync(inputs, CancellationToken.None);

        result.Count.ShouldBe(inputs.Length);
        foreach (var vec in result)
        {
            vec.Length.ShouldBe(8);
        }
    }

    [Fact]
    public async Task EmbedAsync_Same_Input_Yields_Same_Vector()
    {
        var embedder = new FakeEmbedder(dimensions: 8);

        var first = await embedder.EmbedAsync(["alpha"], CancellationToken.None);
        var second = await embedder.EmbedAsync(["alpha"], CancellationToken.None);

        first[0].ToArray().ShouldBe(second[0].ToArray());
    }

    [Fact]
    public async Task EmbedAsync_Different_Inputs_Yield_Different_Vectors()
    {
        var embedder = new FakeEmbedder(dimensions: 8);

        var result = await embedder.EmbedAsync(["alpha", "beta"], CancellationToken.None);

        result[0].ToArray().ShouldNotBe(result[1].ToArray());
    }

    [Fact]
    public async Task EmbedAsync_Null_List_Throws_ArgumentNullException()
    {
        var embedder = new FakeEmbedder();

        await Should.ThrowAsync<ArgumentNullException>(
            () => embedder.EmbedAsync(null!, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task EmbedAsync_Whitespace_Entry_Throws_ArgumentException(string bad)
    {
        var embedder = new FakeEmbedder();

        await Should.ThrowAsync<ArgumentException>(
            () => embedder.EmbedAsync(["ok", bad, "also-ok"], CancellationToken.None));
    }

    [Fact]
    public async Task EmbedAsync_Null_Entry_Throws_ArgumentException()
    {
        var embedder = new FakeEmbedder();

        await Should.ThrowAsync<ArgumentException>(
            () => embedder.EmbedAsync(["ok", null!, "also-ok"], CancellationToken.None));
    }

    [Fact]
    public async Task EmbedAsync_Honors_Cancellation()
    {
        var embedder = new FakeEmbedder();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => embedder.EmbedAsync(["alpha"], cts.Token));
    }

    [Fact]
    public async Task EmbedAsync_Respects_Configured_Dimensions()
    {
        var embedder = new FakeEmbedder(dimensions: 16);

        var result = await embedder.EmbedAsync(["alpha"], CancellationToken.None);

        result[0].Length.ShouldBe(16);
    }
}
